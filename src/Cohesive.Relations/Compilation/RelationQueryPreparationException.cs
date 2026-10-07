using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Compilation;

/// <summary>Preparation failure retaining the original structured compiler evidence.</summary>
/// <remarks>Unreleased API migration: derives from PreparationException/InvalidOperationException, not
/// ArgumentException. Catch this type or PreparationException. Default codes now distinguish semantic and
/// physical failures; explicit relationQuery.subplan.* codes are unchanged. See the Relations README.</remarks>
public sealed class RelationQueryPreparationException : PreparationException
{
    /// <summary>Creates a semantic or physical preparation failure.</summary>
    /// <param name="phase">Preparation phase that failed.</param>
    /// <param name="compilation">Exact semantic compilation result.</param>
    /// <param name="physical">Physical planning result, when reached.</param>
    /// <param name="code">Stable preparation diagnostic code.</param>
    /// <param name="detail">Optional explanation of a rejected preparation boundary.</param>
    public RelationQueryPreparationException(string phase, RelationQueryCompilationResult compilation,
        RelationQueryPhysicalPlanningResult? physical = null, string? code = null,
        string? detail = null) : base(phase, code ?? (physical is null ? "relationQuery.preparation.semantic" : "relationQuery.preparation.physical"), detail ?? $"Relation query preparation failed during {phase}.")
    { Compilation = compilation; Physical = physical; }
    /// <summary>Semantic evidence including original diagnostics.</summary>
    public RelationQueryCompilationResult Compilation { get; }
    /// <summary>Physical evidence including original diagnostics, if available.</summary>
    public RelationQueryPhysicalPlanningResult? Physical { get; }
}
