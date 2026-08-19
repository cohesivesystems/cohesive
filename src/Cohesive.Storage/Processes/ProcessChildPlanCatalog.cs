using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;

namespace Cohesive.Storage.Processes;

/// <summary>Immutable exact-reference catalog of compiled child Process plans.</summary>
public sealed class ProcessChildPlanCatalog : IProcessChildPlanResolver
{
    readonly ImmutableDictionary<ExecutionDefinitionReference, CompiledProcessPlan> plans;

    /// <summary>Creates an immutable child-plan catalog.</summary>
    /// <param name="plans">Compiled Process plans deployed as child targets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plans"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A plan is null, repeats an exact definition, or conflicts with another fingerprint for the same definition
    /// identity and revision.
    /// </exception>
    public ProcessChildPlanCatalog(IEnumerable<CompiledProcessPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var builder = ImmutableDictionary.CreateBuilder<ExecutionDefinitionReference, CompiledProcessPlan>();
        Dictionary<(ExecutionDefinitionId Definition, ExecutionRevisionId Revision), ExecutionDefinitionReference>
            revisions = [];
        foreach (var plan in plans)
        {
            if (plan is null)
                throw new ArgumentException("A child Process plan catalog cannot contain null entries.", nameof(plans));
            var definition = plan.DefinitionReference;
            var revisionKey = (definition.DefinitionId, definition.RevisionId);
            if (revisions.TryGetValue(revisionKey, out var retained)
                && retained.Fingerprint != definition.Fingerprint)
            {
                throw new ArgumentException(
                    $"Child Process '{definition.DefinitionId.Value}' revision "
                    + $"'{definition.RevisionId.Value}' has conflicting fingerprints.",
                    nameof(plans));
            }
            revisions[revisionKey] = definition;
            if (!builder.TryAdd(definition, plan))
            {
                throw new ArgumentException(
                    $"Exact child Process '{definition.DefinitionId.Value}' is registered more than once.",
                    nameof(plans));
            }
        }
        this.plans = builder.ToImmutable();
    }

    /// <summary>Number of exact child Process definitions in the catalog.</summary>
    public int Count => plans.Count;

    /// <inheritdoc />
    public bool TryResolve(ExecutionDefinitionReference definition, out CompiledProcessPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return plans.TryGetValue(definition, out plan);
    }
}
