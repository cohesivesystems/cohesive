using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Immutable exact-reference catalog of precompiled Process plans admitted for bounded execution.</summary>
/// <remarks>
/// This catalog is a worker deployment projection, not definition authority. Every entry retains its canonical
/// document and compiled plan, and lookup requires the complete definition identity, revision, and fingerprint.
/// A worker restart must rebuild an equivalent catalog before it can replay an in-flight orchestration.
/// </remarks>
public sealed class DurableTaskSequentialProcessPlanCatalog
{
    readonly ImmutableDictionary<ExecutionDefinitionReference, DurableTaskProcessRealizationPlan> plans;

    /// <summary>Creates an immutable catalog from completely planned canonical Processes.</summary>
    /// <param name="plans">Exact Durable Task realization plans deployed to this worker.</param>
    /// <param name="bindingResolver">
    /// Deterministic exact Request binding resolver used during orchestration replay. The default leaves Requests
    /// as external interactions instead of automatically dispatching them.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="plans"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is null, repeats an exact reference, or includes constructs outside the bounded executable slice.
    /// </exception>
    public DurableTaskSequentialProcessPlanCatalog(
        IEnumerable<DurableTaskProcessRealizationPlan> plans,
        IDurableRequestBindingResolver? bindingResolver = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var builder = ImmutableDictionary.CreateBuilder<ExecutionDefinitionReference, DurableTaskProcessRealizationPlan>();
        Dictionary<(ExecutionDefinitionId Definition, ExecutionRevisionId Revision), ExecutionDefinitionReference>
            revisions = [];
        foreach (var plan in plans)
        {
            if (plan is null)
            {
                throw new ArgumentException("A Process plan catalog cannot contain null entries.", nameof(plans));
            }

            DurableTaskSequentialProcessEligibility.Require(plan);
            var revisionKey = (plan.Definition.DefinitionId, plan.Definition.RevisionId);
            if (revisions.TryGetValue(revisionKey, out var retained)
                && retained.Fingerprint != plan.Definition.Fingerprint)
            {
                throw new ArgumentException(
                    $"Process definition '{plan.Definition.DefinitionId.Value}' revision "
                    + $"'{plan.Definition.RevisionId.Value}' is deployed with conflicting fingerprints.",
                    nameof(plans));
            }
            revisions[revisionKey] = plan.Definition;
            if (!builder.TryAdd(plan.Definition, plan))
            {
                throw new ArgumentException(
                    $"Process definition '{plan.Definition.DefinitionId.Value}' revision "
                    + $"'{plan.Definition.RevisionId.Value}' is deployed more than once with the same fingerprint.",
                    nameof(plans));
            }
        }

        this.plans = builder.ToImmutable();
        BindingResolver = bindingResolver ?? EmptyDurableRequestBindingResolver.Instance;
    }

    /// <summary>Number of exact Process plans deployed to the worker.</summary>
    public int Count => plans.Count;

    internal IDurableRequestBindingResolver BindingResolver { get; }

    /// <summary>Resolves the precompiled plan matching one complete canonical definition reference.</summary>
    /// <param name="definition">Exact definition identity, revision, and fingerprint.</param>
    /// <returns>The matching realization plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">No exact deployed plan matches <paramref name="definition"/>.</exception>
    public DurableTaskProcessRealizationPlan GetExact(ExecutionDefinitionReference definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return plans.TryGetValue(definition, out var plan)
            ? plan
            : throw new KeyNotFoundException(
                $"No Durable Task Process plan is deployed for exact definition "
                + $"'{definition.DefinitionId.Value}' revision '{definition.RevisionId.Value}' fingerprint "
                + $"'{definition.Fingerprint.Value}'.");
    }
}

static class DurableTaskSequentialProcessEligibility
{
    internal static void Require(DurableTaskProcessRealizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<string> unsupported = [];
        foreach (var node in plan.CanonicalPlan.Definition.Nodes)
        {
            if (node is not (InvokeTransitionProcessNode
                or EvaluateRelationProcessNode
                or RequestProcessNode
                or ChoiceProcessNode
                or MatchProcessNode
                or ForkProcessNode
                or JoinProcessNode
                or TimerProcessNode
                or DurableCutProcessNode
                or InvokeProcessProcessNode
                or ForEachPartitionProcessNode
                or RepeatAcrossActivationProcessNode
                or ReturnProcessNode
                or FailProcessNode))
            {
                unsupported.Add($"{node.Id.Value}:{ProcessNodeConstructCatalog.GetRequirement(node).Name}");
            }
        }

        if (unsupported.Count > 0)
        {
            throw new ArgumentException(
                "The Durable Task Process interpreter cannot execute: "
                + string.Join(", ", unsupported),
                nameof(plan));
        }
    }
}
