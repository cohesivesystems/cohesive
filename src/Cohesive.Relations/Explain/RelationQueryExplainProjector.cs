using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Explain;

/// <summary>Projects retained canonical lifecycle artifacts into the portable relation/query explain contract.</summary>
public static class RelationQueryExplainProjector
{
    /// <summary>
    /// Projects a target compiler's native artifacts into the portable explanation contract in one pre-sized pass.
    /// </summary>
    /// <typeparam name="TArtifact">Target-native compiled artifact type.</typeparam>
    /// <param name="status">Native compiler terminal status.</param>
    /// <param name="artifacts">Native artifacts in their deterministic compiler order.</param>
    /// <param name="diagnostics">Target-neutral native-compilation diagnostics.</param>
    /// <param name="projectArtifact">
    /// Adapter projection from one native artifact to its payload-free portable identity and provenance reference.
    /// </param>
    /// <returns>An adapter-neutral native-compilation explanation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projectArtifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The projected status, artifacts, or diagnostics conflict.</exception>
    public static RelationQueryNativeCompilationExplanation ProjectNativeCompilation<TArtifact>(
        RelationQueryNativeCompilationStatus status,
        ImmutableArray<TArtifact> artifacts,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics,
        Func<TArtifact, RelationQueryNativeArtifactReference> projectArtifact)
    {
        ArgumentNullException.ThrowIfNull(projectArtifact);
        var normalizedArtifacts = artifacts.IsDefault ? [] : artifacts;
        ImmutableArray<RelationQueryNativeArtifactReference>.Builder projected =
            ImmutableArray.CreateBuilder<RelationQueryNativeArtifactReference>(normalizedArtifacts.Length);
        foreach (var artifact in normalizedArtifacts)
            projected.Add(projectArtifact(artifact));
        return new(status, projected.MoveToImmutable(), diagnostics);
    }

    /// <summary>
    /// Projects and attributes a target compiler's native result to the exact request that was attempted.
    /// </summary>
    /// <typeparam name="TArtifact">Target-native compiled artifact type.</typeparam>
    /// <param name="request">Exact target-neutral request supplied to native lowering.</param>
    /// <param name="status">Native compiler terminal status.</param>
    /// <param name="artifacts">Native artifacts in their deterministic compiler order.</param>
    /// <param name="diagnostics">Target-neutral native-compilation diagnostics.</param>
    /// <param name="projectArtifact">Projection from one native artifact to its payload-free reference.</param>
    /// <returns>An attributed native-compilation explain stage.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="projectArtifact"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The projected result or request attribution is inconsistent.</exception>
    public static RelationQueryNativeCompilationExplainStage ProjectNativeCompilation<TArtifact>(
        RelationQueryNativeCompilationRequest request,
        RelationQueryNativeCompilationStatus status,
        ImmutableArray<TArtifact> artifacts,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics,
        Func<TArtifact, RelationQueryNativeArtifactReference> projectArtifact)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RelationQueryNativeCompilationExplainStage.Create(
            request,
            ProjectNativeCompilation(status, artifacts, diagnostics, projectArtifact));
    }

    /// <summary>Projects every supplied lifecycle artifact without rerunning semantic compilation or execution.</summary>
    /// <param name="compilation">Required target-independent static-compilation result.</param>
    /// <param name="profileFeasibility">Optional target-profile feasibility report.</param>
    /// <param name="placement">Optional exact source-placement artifact.</param>
    /// <param name="boundRealization">Optional contextual bound-realization report.</param>
    /// <param name="physicalPlanning">Optional physical-planning result.</param>
    /// <param name="nativeCompilation">Optional fully attributed backend-native compilation stage.</param>
    /// <param name="evaluation">Optional sanitized runtime evaluation summary.</param>
    /// <param name="physicalPlanningPolicy">
    /// Planning policy supplied to an unsuccessful physical-planning attempt when the result itself cannot retain it.
    /// </param>
    /// <returns>A normalized explain artifact containing exactly the available lifecycle stages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Supplied stages are incomplete, out of affinity, or conflict with their normalized statuses.
    /// </exception>
    /// <exception cref="InvalidOperationException">A successful static plan cannot be projected consistently.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical explain content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static RelationQueryExplainArtifact Project(
        RelationQueryCompilationResult compilation,
        RelationQueryRealizationReport? profileFeasibility = null,
        RelationQuerySourcePlacement? placement = null,
        RelationQueryBoundRealizationReport? boundRealization = null,
        RelationQueryPhysicalPlanningResult? physicalPlanning = null,
        RelationQueryNativeCompilationExplainStage? nativeCompilation = null,
        RelationQueryEvaluationExplanation? evaluation = null,
        RelationQueryPhysicalPlanningPolicy? physicalPlanningPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var observability = profileFeasibility?.Observability
            ?? RelationQueryResultObservability.ExactContributors;
        List<RelationQueryExplainStage> stages = [ProjectStatic(compilation, observability)];
        if (profileFeasibility is not null)
        {
            stages.Add(new RelationQueryProfileFeasibilityExplainStage(
                RelationQueryExplainStatus.FromRealization(profileFeasibility.Status),
                profileFeasibility));
        }

        if (placement is not null)
        {
            var expected = compilation.Plan is null
                ? null
                : RelationQueryCompiledPlanReference.From(compilation.Plan);
            var status = expected is not null && RelationQueryExplainAffinity.SamePlan(expected, placement.Plan)
                ? RelationQueryExplainStageStatus.Complete
                : RelationQueryExplainStageStatus.Invalid;
            stages.Add(new RelationQuerySourcePlacementExplainStage(status, placement));
        }

        if (boundRealization is not null)
        {
            stages.Add(new RelationQueryBoundRealizationExplainStage(
                RelationQueryExplainStatus.FromRealization(boundRealization.Status),
                boundRealization));
        }

        if (physicalPlanning is not null)
        {
            if (compilation.Plan is null || profileFeasibility is null || placement is null)
            {
                throw new ArgumentException(
                    "Physical planning requires successful static compilation, profile feasibility, and placement.",
                    nameof(physicalPlanning));
            }

            stages.Add(new RelationQueryPhysicalPlanningExplainStage(
                RelationQueryExplainStatus.FromPhysical(physicalPlanning.Status),
                RelationQueryCompiledPlanReference.From(compilation.Plan),
                profileFeasibility.Fingerprint,
                placement.Fingerprint,
                physicalPlanning.Plan?.Policy ?? physicalPlanningPolicy,
                physicalPlanning));
        }

        if (nativeCompilation is not null)
            stages.Add(nativeCompilation);

        if (evaluation is not null)
        {
            stages.Add(new RelationQueryEvaluationExplainStage(
                RelationQueryExplainStatus.FromExecution(evaluation.Status),
                evaluation));
        }

        return new(RelationQueryExplainArtifact.CurrentSchemaVersion, [.. stages]);
    }

    /// <summary>Projects the exact phase chain retained by one canonical evaluation outcome.</summary>
    /// <param name="outcome">Terminal canonical evaluation outcome.</param>
    /// <returns>
    /// A normalized explain artifact through the latest phase reached by <paramref name="outcome"/>, including a
    /// sanitized evaluation stage whenever static compilation produced a plan.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Retained phase artifacts violate explain-stage affinity.</exception>
    /// <exception cref="InvalidOperationException">Runtime terminal attribution cannot be matched to the static branches.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical explain content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Explain content contains an unsupported serialization type.</exception>
    public static RelationQueryExplainArtifact Project(RelationQueryEvaluationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Compilation.Plan is null)
        {
            return Project(
                outcome.Compilation,
                evaluation: new(
                    outcome.Evaluation.Fingerprint,
                    null,
                    RelationQueryExecutionStatus.Failed,
                    [],
                    [],
                    ProjectEvaluationDiagnostics(outcome)));
        }

        var plan = outcome.Compilation.Plan;
        var reference = RelationQueryCompiledPlanReference.From(plan);
        var branches = RelationQueryNativeCompilationRequest.CreateBranches(plan.ExecutionSlice);
        var evaluation = ProjectEvaluation(reference, branches, outcome);
        return Project(
            outcome.Compilation,
            outcome.Realization,
            outcome.Placement,
            physicalPlanning: outcome.PhysicalPlanning,
            evaluation: evaluation);
    }

    static RelationQueryStaticCompilationExplainStage ProjectStatic(
        RelationQueryCompilationResult compilation,
        RelationQueryResultObservability observability)
    {
        RelationQueryStaticPlanExplanation? explanation = null;
        if (compilation.Plan is { } plan)
        {
            explanation = new(
                RelationQueryCompiledPlanReference.From(plan),
                plan.LogicalPlan,
                RelationQueryExplainRequirementGraph.From(plan.RequirementGraph),
                RelationQueryNativeCompilationRequest.CreateBranches(plan.ExecutionSlice),
                observability,
                RelationQueryRealizationRequirementProjector.Project(plan, observability));
        }

        return new(
            compilation.IsSuccessful
                ? RelationQueryExplainStageStatus.Complete
                : RelationQueryExplainStageStatus.Invalid,
            RelationQueryCompilationRequestReference.From(compilation.Request),
            explanation,
            compilation.Diagnostics);
    }

    static RelationQueryEvaluationExplanation ProjectEvaluation(
        RelationQueryCompiledPlanReference plan,
        ImmutableArray<RelationQueryNativeResultBranch> branches,
        RelationQueryEvaluationOutcome outcome)
    {
        var result = outcome.Result;
        ImmutableArray<RelationQueryExplainResultSummary> results = result is null
            ? []
            : ProjectResults(branches, result);
        ImmutableArray<RelationQueryExplainRequirementGapSummary> gaps = result is null
            ? []
            : ProjectGaps(result);
        return new(
            outcome.Evaluation.Fingerprint,
            plan,
            outcome.Status,
            results,
            gaps,
            ProjectEvaluationDiagnostics(outcome));
    }

    static ImmutableArray<RelationQueryExplainResultSummary> ProjectResults(
        ImmutableArray<RelationQueryNativeResultBranch> branches,
        RelationQueryExecutionResult result)
    {
        ImmutableArray<RelationQueryExplainResultSummary>.Builder summaries =
            ImmutableArray.CreateBuilder<RelationQueryExplainResultSummary>(
                result.Relation is null ? result.QueryResults.Length : 1);
        if (result.Relation is { } relation)
        {
            var branch = branches.SingleOrDefault(candidate => candidate.Relation == relation.Relation)
                ?? throw new InvalidOperationException(
                    $"No static result branch represents relation '{relation.Relation.Value}'.");
            summaries.Add(new(
                branch.Id,
                RelationQueryExecutionResultKind.Rows,
                relation.Shape,
                relation.State,
                relation.Rows.Length));
        }
        else
        {
            foreach (var named in result.QueryResults)
            {
                var branch = branches.SingleOrDefault(candidate => candidate.QueryResult == named.Result)
                    ?? throw new InvalidOperationException(
                        $"No static result branch represents query result '{named.Result.Value}'.");
                summaries.Add(new(branch.Id, named.Kind, named.Shape, named.State, named.Rows.Length));
            }
        }

        return summaries.MoveToImmutable();
    }

    static ImmutableArray<RelationQueryExplainRequirementGapSummary> ProjectGaps(
        RelationQueryExecutionResult result)
    {
        Dictionary<GapGroupKey, GapGroup> groups = new(result.RequirementGapAnalysis.Gaps.Length);
        foreach (var gap in result.RequirementGapAnalysis.Gaps)
        {
            ImmutableArray<RelationQueryOutputId> outputs =
            [
                .. gap.Impacts.Select(static impact => impact.Output.Id)
                    .Distinct()
                    .OrderBy(static output => output.Value, StringComparer.Ordinal)
            ];
            ImmutableArray<RelationRequirementGapResolutionKind> resolutions =
            [
                .. gap.SuggestedResolutions.Distinct().Order()
            ];
            var key = new GapGroupKey(
                gap.Cause,
                gap.Input.Id.Value,
                string.Join('\u001f', outputs.Select(static output => output.Value)),
                string.Join('\u001f', resolutions.Select(static resolution => ((int)resolution).ToString())));
            if (groups.TryGetValue(key, out var group))
                group.Count++;
            else
                groups.Add(key, new(gap.Input.Id, outputs, resolutions));
        }

        ImmutableArray<RelationQueryExplainRequirementGapSummary>.Builder summaries =
            ImmutableArray.CreateBuilder<RelationQueryExplainRequirementGapSummary>(groups.Count);
        foreach (var pair in groups)
        {
            summaries.Add(new(
                pair.Key.Cause,
                pair.Value.Input,
                pair.Value.Outputs,
                pair.Value.Count,
                pair.Value.Resolutions));
        }
        return RelationQueryExplainOrdering.OrderGapSummaries(summaries.MoveToImmutable());
    }

    static ImmutableArray<RelationQueryExplainDiagnostic> ProjectEvaluationDiagnostics(
        RelationQueryEvaluationOutcome outcome)
    {
        var capacity = outcome.Diagnostics.Length
            + (outcome.PhysicalExecution?.Diagnostics.Length ?? 0)
            + (outcome.Result?.Diagnostics.Length ?? 0);
        ImmutableArray<RelationQueryExplainDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<RelationQueryExplainDiagnostic>(capacity);
        foreach (var diagnostic in outcome.Diagnostics)
        {
            diagnostics.Add(new(
                RelationQueryExplainStageWireNames.Evaluation,
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.Message));
        }

        if (outcome.PhysicalExecution is { } execution)
        {
            foreach (var diagnostic in execution.Diagnostics)
            {
                diagnostics.Add(new(
                    RelationQueryExplainStageWireNames.Evaluation,
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Message,
                    input: diagnostic.Input,
                    physicalStage: diagnostic.Stage,
                    source: diagnostic.Source));
            }
        }

        if (outcome.Result is { } result)
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(new(
                    RelationQueryExplainStageWireNames.Evaluation,
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Message,
                    node: diagnostic.Node,
                    input: diagnostic.Input,
                    output: diagnostic.Output?.Id,
                    semanticSite: diagnostic.SemanticSite));
            }
        }

        return RelationQueryExplainOrdering.OrderDiagnostics(diagnostics.MoveToImmutable());
    }

    readonly record struct GapGroupKey(
        RelationRequirementGapCause Cause,
        string Input,
        string Outputs,
        string Resolutions);

    sealed class GapGroup(
        RelationQueryInputId input,
        ImmutableArray<RelationQueryOutputId> outputs,
        ImmutableArray<RelationRequirementGapResolutionKind> resolutions)
    {
        public RelationQueryInputId Input { get; } = input;

        public ImmutableArray<RelationQueryOutputId> Outputs { get; } = outputs;

        public ImmutableArray<RelationRequirementGapResolutionKind> Resolutions { get; } = resolutions;

        public int Count { get; set; } = 1;
    }
}
