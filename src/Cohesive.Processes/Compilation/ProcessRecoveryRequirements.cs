using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Processes.Compilation;

/// <summary>Recovery-focused projection of the canonical interpreter requirement catalog.</summary>
/// <remarks>These requirements describe continuation-preserving worker replacement with receipted entity
/// transitions. They do not establish guarantees for arbitrary external effects or replace full target compilation.</remarks>
public static class ProcessRecoveryRequirements
{
    /// <summary>Explicit source-selected guarantees for checkpoint restoration and fenced transition recovery.</summary>
    public static ImmutableArray<ProcessInterpreterRequirementKey> WorkerTakeover { get; } =
    [
        ProcessInterpreterGuarantees.CheckpointPersistence,
        ProcessInterpreterGuarantees.ContinuationRestoration,
        ProcessInterpreterGuarantees.TransitionReceiptReplay,
        ProcessInterpreterGuarantees.TransitionEffectFencing
    ];

    /// <summary>Projects only demanded recovery guarantees from the exact plan's inventory.</summary>
    /// <param name="plan">Compiled source and explicit compilation policy.</param>
    /// <returns>A recovery-only inventory retaining exact definition and source-node evidence.</returns>
    /// <exception cref="ArgumentNullException">The plan is null.</exception>
    public static ProcessInterpreterRequirementInventory Collect(CompiledProcessPlan plan)
    {
        var inventory = ProcessInterpreterRequirementCollector.Collect(plan);
        return new(inventory.Definition, [.. inventory.Requirements.Where(requirement => WorkerTakeover.Contains(requirement.Key))],
            ProcessExecutionLifetime.Durable);
    }

    /// <summary>Matches recovery requirements without claiming support for other process constructs.</summary>
    /// <param name="plan">Exact compiled source and requested guarantees.</param>
    /// <param name="profile">Adapter-owned recovery capability evidence.</param>
    /// <returns>Structured decisions, constraints and actionable unsupported-requirement diagnostics.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ProcessInterpreterRealizationReport Assess(CompiledProcessPlan plan, ProcessInterpreterCapabilityProfile profile)
        => ProcessInterpreterRealizationCompiler.Compile(Collect(plan), profile);

    /// <summary>Explicit unavailable evidence when a host has supplied no recovery qualification.</summary>
    public static ProcessInterpreterCapabilityProfile Unavailable { get; } = new(
        new("process/recovery/unavailable/v1"), new("process/unqualified-recovery-host"),
        [.. WorkerTakeover.Select(key => new ProcessInterpreterCapabilityEvidence(new("unavailable/" + key.Name), key,
            CapabilityRealizationKind.Unavailable))]);
}
