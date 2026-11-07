using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Mapping;

/// <summary>A prepared canonical relation and DTO materializer, independent of source placement or acquisition.</summary>
/// <typeparam name="TOutput">CLR DTO type.</typeparam>
/// <remarks>Safe to share across calls. Evaluators own placement, source readers, limits and target guarantees.
/// No join, cardinality, missing-value, aggregate or provenance semantics are added by this facade.</remarks>
public sealed class RelationDtoMapping<TOutput> where TOutput : notnull
{
    internal RelationDtoMapping(RelationQueryCompilationResult compilation, CompiledRelationDtoMapper<TOutput> mapper,
        RelationQueryAuthoringManifest authoringProvenance)
    { Compilation = compilation; Mapper = mapper; AuthoringProvenance = authoringProvenance; }

    /// <summary>Exact portable definition, shapes and output demand.</summary>
    public RelationQueryCompilationRequest CompilationRequest => Compilation.Request;
    /// <summary>Prepared static plan and structured semantic diagnostics.</summary>
    public RelationQueryCompilationResult Compilation { get; }
    /// <summary>DTO binding decisions and provenance-preserving materialization APIs.</summary>
    public CompiledRelationDtoMapper<TOutput> Mapper { get; }
    /// <summary>Original expression-lowering decisions and source references.</summary>
    public RelationQueryAuthoringManifest AuthoringProvenance { get; }

    /// <summary>Starts an invocation of this exact prepared snapshot.</summary>
    /// <param name="evaluation">Explicit caller-owned invocation identity.</param>
    /// <returns>The existing evaluation builder; callers may supply roots or parameter values as required.</returns>
    /// <exception cref="ArgumentException">The evaluation identity is invalid.</exception>
    public RelationQueryEvaluationBuilder Evaluate(RelationQueryEvaluationId evaluation) => new(CompilationRequest, evaluation);

    /// <summary>Evaluates using explicit source placement, readers, limits and policies.</summary>
    /// <param name="evaluator">Caller-owned evaluator; no client or source binding is inferred.</param>
    /// <param name="evaluation">Invocation built from this mapping's exact compilation request.</param>
    /// <param name="cancellationToken">Cancels acquisition and canonical interpretation.</param>
    /// <returns>The complete phase outcome with structured diagnostics, including failed or inconclusive outcomes.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The invocation belongs to another request, or provider inputs are invalid.</exception>
    /// <exception cref="InvalidOperationException">Placement or provider state is incompatible with the declared request.</exception>
    /// <exception cref="OperationCanceledException">Execution is canceled.</exception>
    public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(IRelationQueryEvaluator evaluator,
        RelationQueryEvaluation evaluation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(evaluation.Compilation, CompilationRequest))
            throw new ArgumentException("The invocation must use this mapping's exact compilation request.", nameof(evaluation));
        return evaluator.EvaluateAsync(evaluation, cancellationToken);
    }

    /// <summary>Evaluates and materializes a complete DTO array, failing without publishing partial results.</summary>
    /// <param name="evaluator">Caller-owned evaluator defining acquisition and resource limits.</param>
    /// <param name="evaluation">Exact invocation, including explicit supplied roots or parameters when needed.</param>
    /// <param name="cancellationToken">Cancels evaluation and DTO materialization.</param>
    /// <returns>Owned DTOs in canonical result order; no additional ordering or default rows are invented.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The invocation belongs to another request, or provider inputs are invalid.</exception>
    /// <exception cref="InvalidOperationException">Evaluation is failed/inconclusive, or strict materialization fails.</exception>
    /// <exception cref="OperationCanceledException">Execution is canceled.</exception>
    /// <remarks>Use EvaluateAsync and Mapper.Map to inspect retained execution, diagnostics and source contributors.</remarks>
    public async Task<TOutput[]> MapAsync(IRelationQueryEvaluator evaluator, RelationQueryEvaluation evaluation,
        CancellationToken cancellationToken = default)
    {
        var outcome = await EvaluateAsync(evaluator, evaluation, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccessful)
            throw new InvalidOperationException($"DTO relation evaluation ended with {outcome.Status}; use EvaluateAsync for phase diagnostics.");
        var mapped = Mapper.Map(outcome.PhysicalExecution!, cancellationToken: cancellationToken);
        if (!mapped.IsSuccessful)
            throw new InvalidOperationException(string.Join(Environment.NewLine, mapped.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var result = new TOutput[mapped.Rows.Length];
        for (var index = 0; index < result.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[index] = mapped.Rows[index].Value;
        }
        return result;
    }

}
