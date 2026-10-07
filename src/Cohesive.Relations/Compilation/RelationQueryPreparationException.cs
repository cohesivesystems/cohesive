using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Compilation;

/// <summary>Preparation failure retaining the original structured compiler evidence.</summary>
public sealed class RelationQueryPreparationException : ArgumentException
{
    /// <summary>Creates a semantic or physical preparation failure.</summary>
    /// <param name="phase">Preparation phase that failed.</param>
    /// <param name="compilation">Exact semantic compilation result.</param>
    /// <param name="physical">Physical planning result, when reached.</param>
    /// <param name="code">Stable preparation diagnostic code.</param>
    /// <param name="detail">Optional explanation of a rejected preparation boundary.</param>
    public RelationQueryPreparationException(string phase, RelationQueryCompilationResult compilation,
        RelationQueryPhysicalPlanningResult? physical = null, string code = "relationQuery.preparation.invalid",
        string? detail = null) : base(detail ?? $"Relation query preparation failed during {phase}.")
    { Phase = phase; Compilation = compilation; Physical = physical; Code = code; }
    /// <summary>Stable machine-readable preparation failure code.</summary>
    public string Code { get; }
    /// <summary>Failed preparation phase.</summary>
    public string Phase { get; }
    /// <summary>Semantic evidence including original diagnostics.</summary>
    public RelationQueryCompilationResult Compilation { get; }
    /// <summary>Physical evidence including original diagnostics, if available.</summary>
    public RelationQueryPhysicalPlanningResult? Physical { get; }
}
