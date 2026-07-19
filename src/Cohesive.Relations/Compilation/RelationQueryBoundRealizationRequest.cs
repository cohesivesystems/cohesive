using System.Collections.Immutable;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Compilation;

/// <summary>
/// Exact target-neutral plan, profile-feasibility, placement, and branch selection supplied to contextual binding.
/// </summary>
public sealed class RelationQueryBoundRealizationRequest
{
    /// <summary>Creates a contextual realization request.</summary>
    /// <param name="plan">Successful demand-scoped static plan.</param>
    /// <param name="profileFeasibility">Family-level target-profile realization report.</param>
    /// <param name="placement">Physical source placement to qualify with adapter evidence.</param>
    /// <param name="branches">Selected branch identities, or a default array to select every branch.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="profileFeasibility"/>, or <paramref name="placement"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="branches"/> is explicitly empty, contains a default or repeated identity, or names a branch
    /// absent from the compiled execution slice.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A shape snapshot cannot be represented by compiled-plan canonicalization.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public RelationQueryBoundRealizationRequest(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport profileFeasibility,
        RelationQuerySourcePlacement placement,
        ImmutableArray<RelationQueryNativeResultBranchId> branches = default)
    {
        Plan = Guard.RequireNotNull(plan);
        ProfileFeasibility = Guard.RequireNotNull(profileFeasibility);
        Placement = Guard.RequireNotNull(placement);
        PlanReference = RelationQueryCompiledPlanReference.From(plan);
        var available = RelationQueryNativeCompilationRequest.CreateBranches(plan.ExecutionSlice);
        if (branches.IsDefault)
        {
            Branches = available;
        }
        else
        {
            if (branches.IsDefaultOrEmpty)
                throw new ArgumentException("An explicit contextual-realization branch selection cannot be empty.", nameof(branches));
            if (branches.Any(static branch => string.IsNullOrWhiteSpace(branch.Value)))
                throw new ArgumentException("Contextual-realization branch identities cannot be default.", nameof(branches));
            if (branches.Distinct().Count() != branches.Length)
                throw new ArgumentException("Contextual-realization branch identities cannot be repeated.", nameof(branches));

            var availableById = available.ToDictionary(static branch => branch.Id);
            if (branches.Any(branch => !availableById.ContainsKey(branch)))
                throw new ArgumentException("Contextual realization selected a branch absent from the compiled plan.", nameof(branches));
            Branches =
            [
                .. branches.OrderBy(static branch => branch.Value, StringComparer.Ordinal)
                    .Select(branch => availableById[branch])
            ];
        }
        Selection = RelationQueryCompilationSelection.Create(Plan, ProfileFeasibility, Placement, Branches);
    }

    /// <summary>Successful target-independent static plan.</summary>
    public CompiledRelationQueryPlan Plan { get; }

    /// <summary>Exact portable reference computed from <see cref="Plan"/>.</summary>
    public RelationQueryCompiledPlanReference PlanReference { get; }

    /// <summary>Family-level target-profile realization report to qualify.</summary>
    public RelationQueryRealizationReport ProfileFeasibility { get; }

    /// <summary>Physical source placement to qualify with adapter evidence.</summary>
    public RelationQuerySourcePlacement Placement { get; }

    /// <summary>Selected demanded branches in stable branch-identity order.</summary>
    public ImmutableArray<RelationQueryNativeResultBranch> Branches { get; }

    /// <summary>
    /// Deterministic per-branch and union scope selected from the exact plan, profile requirements, and placement.
    /// </summary>
    public RelationQueryCompilationSelection Selection { get; }

    /// <summary>Gets profile requirements that apply to one selected branch.</summary>
    /// <param name="branch">Selected demanded branch to examine.</param>
    /// <returns>
    /// Requirements used by the branch's demanded outputs, plus plan-wide requirements with no explicit uses, in
    /// canonical requirement order.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="branch"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="branch"/> is not selected by this contextual-realization request.
    /// </exception>
    public ImmutableArray<RelationQueryRealizationRequirement> GetRequirementsForBranch(
        RelationQueryNativeResultBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var selected = Branches.SingleOrDefault(candidate => candidate.Id == branch.Id);
        if (selected is null || !Equals(selected, branch))
        {
            throw new ArgumentException(
                "Contextual requirement projection requires an exact branch selected by the request.",
                nameof(branch));
        }

        return Selection.GetBranch(branch.Id).Requirements;
    }

    /// <summary>Validates exact plan alignment and profile feasibility before adapter evidence is projected.</summary>
    /// <returns>Structured error diagnostics; an empty array means contextual projection may proceed.</returns>
    public ImmutableArray<RelationQueryNativeCompilationDiagnostic> ValidateInputs() =>
        RelationQueryNativeCompilationInputValidator.Validate(
            Plan,
            ProfileFeasibility,
            Placement,
            requireRealizable: false);
}

internal static class RelationQueryNativeCompilationInputValidator
{
    public static ImmutableArray<RelationQueryNativeCompilationDiagnostic> Validate(
        CompiledRelationQueryPlan plan,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement,
        bool requireRealizable)
    {
        ImmutableArray<RelationQueryNativeCompilationDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        var realizationMismatches = realization.Plan.GetMismatchedComponents(plan);
        if (!realizationMismatches.IsDefaultOrEmpty)
        {
            diagnostics.Add(new(
                RelationQueryNativeCompilationDiagnosticCodes.RealizationPlanMismatch,
                DiagnosticSeverity.Error,
                $"The realization report differs from the compiled plan in: {string.Join(", ", realizationMismatches)}."));
        }
        var placementMismatches = placement.Plan.GetMismatchedComponents(plan);
        if (!placementMismatches.IsDefaultOrEmpty)
        {
            diagnostics.Add(new(
                RelationQueryNativeCompilationDiagnosticCodes.PlacementPlanMismatch,
                DiagnosticSeverity.Error,
                $"The source placement differs from the compiled plan in: {string.Join(", ", placementMismatches)}."));
        }
        if (requireRealizable && !realization.IsRealizable)
        {
            diagnostics.Add(new(
                RelationQueryNativeCompilationDiagnosticCodes.RealizationUnavailable,
                DiagnosticSeverity.Error,
                $"The realization report status is '{realization.Status}' and cannot justify exact native compilation."));
        }
        return diagnostics.ToImmutable();
    }
}
