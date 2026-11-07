using Cohesive.Relations.Physical;
using Cohesive.Relations.Mapping;
using System.Collections.Immutable;

namespace Cohesive.Relations.Compilation;

/// <summary>Preparation failure retaining the original structured compiler evidence.</summary>
/// <remarks>Unreleased API migration: derives from PreparationException/InvalidOperationException, not
/// ArgumentException. Catch this type or PreparationException. Default codes distinguish semantic, physical and
/// DTO-materialization failures; explicit relationQuery.subplan.* codes are unchanged. See the Relations README.</remarks>
public sealed class RelationQueryPreparationException : PreparationException
{
    /// <summary>Creates a semantic, physical or DTO-materialization preparation failure.</summary>
    /// <param name="phase">Preparation phase that failed.</param>
    /// <param name="compilation">Exact semantic compilation result.</param>
    /// <param name="physical">Physical planning result, when reached.</param>
    /// <param name="code">Stable preparation diagnostic code.</param>
    /// <param name="detail">Optional explanation of a rejected preparation boundary.</param>
    /// <param name="dtoDiagnostics">Original DTO binding diagnostics when materialization preparation failed.</param>
    public RelationQueryPreparationException(string phase, RelationQueryCompilationResult compilation,
        RelationQueryPhysicalPlanningResult? physical = null, string? code = null,
        string? detail = null, ImmutableArray<RelationDtoMapperDiagnostic> dtoDiagnostics = default) : base(phase, code ?? (!dtoDiagnostics.IsDefaultOrEmpty ? "relationQuery.preparation.dto" : physical is null ? "relationQuery.preparation.semantic" : "relationQuery.preparation.physical"), detail ?? $"Relation query preparation failed during {phase}.")
    {
        Compilation = compilation;
        Physical = physical;
        DtoDiagnostics = dtoDiagnostics.IsDefault ? [] : dtoDiagnostics;
    }
    /// <summary>Semantic evidence including original diagnostics.</summary>
    public RelationQueryCompilationResult Compilation { get; }
    /// <summary>Physical evidence including original diagnostics, if available.</summary>
    public RelationQueryPhysicalPlanningResult? Physical { get; }
    /// <summary>Original structured DTO binding diagnostics, empty when that phase did not fail.</summary>
    public ImmutableArray<RelationDtoMapperDiagnostic> DtoDiagnostics { get; }
}
