using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Physical;

/// <summary>One immutable, validated interpreter/physical preparation reusable at runtime binding.</summary>
public sealed class RelationQueryPhysicalPreparation
{
    /// <summary>Validates bounded placement using the canonical physical planner, without acquisition.</summary>
    /// <param name="compilation">Successful semantic compilation.</param><param name="placement">Exact source placement.</param>
    /// <param name="policy">Explicit execution limits and lowering policy.</param>
    /// <exception cref="RelationQueryPreparationException">Semantic or physical preparation fails.</exception>
    public RelationQueryPhysicalPreparation(RelationQueryCompilationResult compilation,
        RelationQuerySourcePlacement placement, RelationQueryPhysicalPlanningPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (!compilation.IsSuccessful) throw new RelationQueryPreparationException("semantic", compilation);
        Compilation = compilation;
        Realization = RelationQueryInMemoryInterpreter.Default.Realize(compilation.Plan!);
        Physical = RelationQueryPhysicalPlanner.Compile(compilation.Plan!, Realization, placement, policy);
        if (!Physical.IsSuccessful) throw new RelationQueryPreparationException("physical", compilation, Physical);
    }
    /// <summary>Exact semantic authority.</summary>
    public RelationQueryCompilationResult Compilation { get; }
    /// <summary>Interpreter capability interpretation.</summary>
    public RelationQueryRealizationReport Realization { get; }
    /// <summary>Successful physical planning evidence.</summary>
    public RelationQueryPhysicalPlanningResult Physical { get; }
}
