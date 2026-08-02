using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Shared target-independent mechanism for executing one exact canonical Relations hydration plan and selecting its
/// demanded output.
/// </summary>
internal sealed class RelationQueryMaterializationHydration
{
    readonly CompiledRelationQueryPlan plan;
    readonly CompiledRelationQueryPhysicalPlan physicalPlan;
    readonly RelationQueryRealizationReport realization;
    readonly RelationQueryOutputReference output;
    readonly RelationQueryPhysicalExecutor executor;
    readonly ImmutableDictionary<RelationQueryInputId, RelationQuerySourceInputContract> relationRoots;
    readonly ImmutableArray<RelationQueryCapabilityEvidence> capabilities;

    /// <summary>Creates one exact Relations hydration mechanism.</summary>
    /// <param name="plan">Exact successful semantic plan.</param>
    /// <param name="physicalPlan">Exact physical plan whose relation-root placements are supplied.</param>
    /// <param name="realization">Exact successful realization report cited by the physical plan.</param>
    /// <param name="output">Complete demanded output selected by the materialization.</param>
    /// <param name="sourceReaders">Readers for non-root hydration inputs.</param>
    /// <exception cref="ArgumentNullException">A required reference or collection is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The output or exact semantic-to-physical plan chain is incompatible.</exception>
    internal RelationQueryMaterializationHydration(
        CompiledRelationQueryPlan plan,
        CompiledRelationQueryPhysicalPlan physicalPlan,
        RelationQueryRealizationReport realization,
        RelationQueryOutputReference output,
        IEnumerable<IRelationQuerySourceReader> sourceReaders)
    {
        this.plan = Guard.RequireNotNull(plan);
        this.physicalPlan = Guard.RequireNotNull(physicalPlan);
        this.realization = Guard.RequireNotNull(realization);
        this.output = Guard.RequireNotNull(output);
        ArgumentNullException.ThrowIfNull(sourceReaders);

        var exactPlan = RelationQueryCompiledPlanReference.From(plan);
        var exactPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(exactPlan);
        if (RelationQueryCompiledPlanReferenceFingerprinter.Compute(physicalPlan.Plan) != exactPlanFingerprint
            || RelationQueryCompiledPlanReferenceFingerprinter.Compute(realization.Plan) != exactPlanFingerprint
            || physicalPlan.Realization != realization.Fingerprint
            || !realization.IsRealizable)
        {
            throw new ArgumentException(
                "Materialization hydration requires one exact realizable semantic, realization, and physical-plan chain.",
                nameof(physicalPlan));
        }

        if (!plan.RequirementGraph.Outputs.Any(candidate => output.Covers(candidate) || candidate.Covers(output)))
            throw new ArgumentException("The selected output is absent from the exact compiled plan.", nameof(output));
        if (output.Field is not null)
            throw new ArgumentException("Materialization hydration requires a complete shaped output.", nameof(output));
        if (!plan.InputContract.Parameters.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Materialization hydration v1 requires parameter values to be bound into the canonical definition.",
                nameof(plan));
        }

        relationRoots = plan.InputContract.Sources
            .Where(static source => source.Role == RelationQuerySourceInputRole.RelationRoot)
            .ToImmutableDictionary(static source => source.Input.Id);
        capabilities = RelationQueryRealizationRuntimeEvidence.ProjectCapabilities(plan, realization);
        executor = new(sourceReaders);
        Plan = exactPlan;
        PhysicalPlan = physicalPlan.Fingerprint;
    }

    /// <summary>Exact compiled Relations plan interpreted by this mechanism.</summary>
    internal RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>Exact physical-plan fingerprint interpreted by this mechanism.</summary>
    internal RelationQueryPhysicalPlanFingerprint PhysicalPlan { get; }

    /// <summary>Canonical relation-root input contracts keyed by compiled input identity.</summary>
    internal IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourceInputContract> RelationRoots => relationRoots;

    /// <summary>Requires one canonical relation root to be supplied by the physical plan.</summary>
    /// <param name="input">Canonical root input.</param>
    /// <param name="parameterName">Public constructor parameter to attribute when validation fails.</param>
    /// <returns>The exact compiled root contract.</returns>
    /// <exception cref="ArgumentException">The input is not a root or its physical placement is not supplied.</exception>
    internal RelationQuerySourceInputContract RequireSuppliedRoot(
        RelationQueryInputId input,
        string parameterName)
    {
        MaterializationContract.RequireDefinedIdentity(input.Value, parameterName);
        if (!relationRoots.TryGetValue(input, out var root))
            throw new ArgumentException("The hydration input must be one canonical relation root.", parameterName);
        var placement = physicalPlan.Placement.Bindings.SingleOrDefault(binding => binding.Input == input);
        if (placement?.Acquisition != RelationQuerySourceAcquisitionKind.Supplied)
            throw new ArgumentException("The hydration physical plan must mark every hydrated root as supplied.", parameterName);
        return root;
    }

    /// <summary>Executes canonical Relations hydration and selects one complete demanded output.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="evaluation">Stable Relations evaluation identity.</param>
    /// <param name="suppliedSources">Complete root-scoped source evidence.</param>
    /// <returns>Complete selected output rows in deterministic interpreter order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Canonical Relations execution or selected output is incomplete.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    internal async ValueTask<ImmutableArray<RelationQueryOutputRow>> HydrateAsync(
        OperationContext context,
        RelationQueryEvaluationId evaluation,
        ImmutableArray<RelationQuerySuppliedSourceInput> suppliedSources)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        var execution = await executor.ExecuteAsync(
                new RelationQueryPhysicalExecutionRequest(
                    plan: plan,
                    physicalPlan: physicalPlan,
                    realization: realization,
                    evaluation: evaluation,
                    suppliedSources: suppliedSources,
                    capabilities: capabilities),
                context.CancellationToken)
            .ConfigureAwait(false);
        if (!execution.IsSuccessful || execution.Interpretation is null)
        {
            var diagnostics = execution.Diagnostics.Select(static diagnostic => diagnostic.Message)
                .Concat(execution.Interpretation?.Diagnostics.Select(static diagnostic => diagnostic.Message) ?? []);
            throw new InvalidOperationException(
                $"Canonical Relations hydration '{evaluation.Value}' was not conclusive ({execution.Status}): "
                + string.Join(" ", diagnostics));
        }

        var rows = output.Kind switch
        {
            RelationQueryOutputReferenceKind.Relation
                when execution.Interpretation.Relation is { } relation
                     && relation.Relation == output.Relation
                     && relation.State == RelationQueryExecutionOutputState.Complete => relation.Rows,
            RelationQueryOutputReferenceKind.QueryResult
                when execution.Interpretation.QueryResults.SingleOrDefault(result => result.Result == output.QueryResult) is { } result
                     && result.State == RelationQueryExecutionOutputState.Complete => result.Rows,
            _ => throw new InvalidOperationException(
                $"Selected Relations output '{output.Id.Value}' was absent or incomplete after hydration.")
        };
        if (rows.Any(static row => !row.IsComplete))
            throw new InvalidOperationException("A materialization cannot retain rows with unresolved Relations gaps.");
        return rows;
    }
}
