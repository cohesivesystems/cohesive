using System.Collections.Immutable;
using Cohesive.Model;
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
    internal static readonly RelationQueryEvaluationId LocalEvaluation = new("cohesive.relations/dto-mapping/local");

    readonly ImmutableDictionary<Type, QualifiedShapeId> sourceTypes;

    internal RelationDtoMapping(RelationQueryCompilationResult compilation, CompiledRelationDtoMapper<TOutput> mapper,
        RelationQueryAuthoringManifest authoringProvenance, ImmutableDictionary<Type, QualifiedShapeId> sourceTypes)
    {
        Compilation = compilation;
        Mapper = mapper;
        AuthoringProvenance = authoringProvenance;
        this.sourceTypes = sourceTypes;
    }

    /// <summary>Exact portable definition, shapes and output demand.</summary>
    public RelationQueryCompilationRequest CompilationRequest => Compilation.Request;
    /// <summary>Prepared static plan and structured semantic diagnostics.</summary>
    public RelationQueryCompilationResult Compilation { get; }
    /// <summary>DTO binding decisions and provenance-preserving materialization APIs.</summary>
    public CompiledRelationDtoMapper<TOutput> Mapper { get; }
    /// <summary>Original expression-lowering decisions and source references.</summary>
    public RelationQueryAuthoringManifest AuthoringProvenance { get; }

    /// <summary>Creates an in-memory evaluator with two acquired collections and default identity bindings.</summary>
    /// <typeparam name="TFirst">Exact authored CLR row type of one acquired input.</typeparam>
    /// <typeparam name="TSecond">Exact authored CLR row type of the other acquired input.</typeparam>
    /// <param name="first">Complete finite collection, snapshotted at evaluator creation.</param>
    /// <param name="second">Complete finite collection, snapshotted at evaluator creation.</param>
    /// <param name="maximumRows">Positive row limit applied to each snapshot and to local execution.</param>
    /// <returns>A reusable evaluator for this prepared request using owned source snapshots.</returns>
    /// <exception cref="ArgumentNullException">A collection or row is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The row limit is not positive.</exception>
    /// <exception cref="ArgumentException">A row type does not select exactly one acquired input, or rows violate their input contract or limit.</exception>
    /// <exception cref="InvalidOperationException">A binding is missing, repeated, lacks a default identity, or requires unsupported placement.</exception>
    /// <exception cref="NotSupportedException">A row cannot be represented as observation data.</exception>
    /// <remarks>Collections are matched by row type. Uses the sole string identity field on each input shape,
    /// including the root, or a string field named Id when the shape declares no identity.
    /// Use the configuration overload for custom keys or a different number of collections.</remarks>
    public IRelationQueryEvaluator CreateInMemoryEvaluator<TFirst, TSecond>(
        IEnumerable<TFirst> first, IEnumerable<TSecond> second,
        int maximumRows = (int)RelationQueryEvaluator.DefaultMaximumRootRows)
        where TFirst : notnull where TSecond : notnull
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return CreateInMemoryEvaluator(sources => sources.Bind(first).Bind(second), maximumRows);
    }

    /// <summary>Creates an evaluator for supplied roots and complete in-memory source sets.</summary>
    /// <param name="configure">Binds each acquired input to a finite collection and optionally overrides the root identity.</param>
    /// <param name="maximumRows">Positive row limit applied to each snapshot and to local execution.</param>
    /// <returns>A reusable evaluator for this prepared request using owned source snapshots and canonical planning.</returns>
    /// <exception cref="ArgumentNullException">The configuration callback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The row limit is not positive.</exception>
    /// <exception cref="ArgumentException">A configured source or its rows violate their contract.</exception>
    /// <exception cref="InvalidOperationException">A binding is missing, placement is invalid, or the plan requires relationship lookup.</exception>
    /// <remarks>The callback runs during preparation. Roots are supplied per invocation. By default the evaluator
    /// uses the root shape's sole string identity field, or its string field named Id when no identity is declared.
    /// Use explicit placement for relationship lookup or multiple acquired inputs sharing a shape.</remarks>
    public IRelationQueryEvaluator CreateInMemoryEvaluator(Action<RelationQueryInMemorySources> configure,
        int maximumRows = (int)RelationQueryEvaluator.DefaultMaximumRootRows)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        var plan = Compilation.Plan!;
        if (!plan.InputContract.Traversals.IsDefaultOrEmpty)
            throw new InvalidOperationException("In-memory collection bindings support source-set enumeration. Relationship traversal requires explicit lookup placement.");
        var sources = new RelationQueryInMemorySources(plan, sourceTypes, maximumRows);
        configure(sources);
        return sources.Build(CompilationRequest);
    }

    /// <summary>Starts an invocation of this exact prepared snapshot.</summary>
    /// <param name="evaluation">Explicit caller-owned invocation identity.</param>
    /// <returns>The existing evaluation builder; callers may supply roots or parameter values as required.</returns>
    /// <exception cref="ArgumentException">The evaluation identity is invalid.</exception>
    public RelationQueryEvaluationBuilder Evaluate(RelationQueryEvaluationId evaluation) => new(CompilationRequest, evaluation);

    /// <summary>Supplies a complete root collection and evaluates it through the existing invocation builder.</summary>
    /// <typeparam name="TRoot">CLR root type, admitted against the prepared relation's root shape.</typeparam>
    /// <param name="evaluator">Caller-owned evaluator providing related sources, limits and target guarantees.</param>
    /// <param name="roots">Finite, non-null root collection, enumerated once. Inputs must remain stable during admission.</param>
    /// <param name="selectIdentity">Selects a nonempty, unique identity for each supplied root.</param>
    /// <param name="evaluation">Explicit invocation identity, or null for the operation-local mapping identity.</param>
    /// <param name="cancellationToken">Cancels enumeration, admission, acquisition and interpretation.</param>
    /// <returns>The full phase outcome, including diagnostics and source provenance.</returns>
    /// <exception cref="ArgumentNullException">A required argument, root or selected identity is null.</exception>
    /// <exception cref="ArgumentException">Root identities are empty/duplicated, root data violates its shape, or the invocation is invalid.</exception>
    /// <exception cref="NotSupportedException">A root cannot be represented as observation data.</exception>
    /// <exception cref="InvalidOperationException">Placement or provider state is incompatible with the declared request.</exception>
    /// <exception cref="OperationCanceledException">The invocation is canceled.</exception>
    /// <remarks>Uses Supply's existing complete-evidence and whole-source partition defaults. Use Evaluate(...)
    /// for partial root evidence, explicit partitions, parameters or other invocation refinements. The local
    /// default identity is not globally unique; provide evaluation when retaining cross-invocation attribution.</remarks>
    public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync<TRoot>(IRelationQueryEvaluator evaluator,
        IEnumerable<TRoot> roots, Func<TRoot, string> selectIdentity,
        RelationQueryEvaluationId? evaluation = null, CancellationToken cancellationToken = default) where TRoot : notnull
        => EvaluateAsync(evaluator, SuppliedEvaluation(evaluator, roots, selectIdentity, evaluation, cancellationToken), cancellationToken);

    /// <summary>Maps a complete root collection using the identity field declared on an in-memory evaluator.</summary>
    /// <typeparam name="TRoot">CLR root type admitted against the canonical root shape.</typeparam>
    /// <param name="evaluator">An in-memory evaluator created for this prepared mapping.</param>
    /// <param name="roots">Finite, non-null roots, enumerated once with cancellation between inputs.</param>
    /// <param name="cancellationToken">Cancels enumeration, admission, acquisition and mapping.</param>
    /// <returns>A complete owned DTO array in canonical result order.</returns>
    /// <exception cref="ArgumentNullException">A required argument or root is null.</exception>
    /// <exception cref="ArgumentException">The evaluator is incompatible, or roots violate their shape or identity contract.</exception>
    /// <exception cref="NotSupportedException">A root cannot be represented as observation data.</exception>
    /// <exception cref="InvalidOperationException">Evaluation is inconclusive or strict materialization fails.</exception>
    /// <exception cref="OperationCanceledException">The invocation is canceled.</exception>
    /// <remarks>Uses the operation-local evaluation identity. Identities come from the admitted observation's
    /// configured field. Use the explicit selector or evaluation builder when supplying invocation metadata.</remarks>
    public Task<TOutput[]> MapAsync<TRoot>(IRelationQueryEvaluator evaluator,
        IEnumerable<TRoot> roots, CancellationToken cancellationToken = default) where TRoot : notnull
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        cancellationToken.ThrowIfCancellationRequested();
        if (evaluator is not RelationQueryInMemorySources.BoundEvaluator bound)
            throw new ArgumentException("Use an evaluator created with CreateInMemoryEvaluator to reuse its root identity.", nameof(evaluator));

        var identityField = bound.RequireRootIdentityField(CompilationRequest, nameof(evaluator));
        var evaluation = Evaluate(LocalEvaluation).SupplyByIdentityField(roots, identityField, cancellationToken).Build();
        return MapAsync(evaluator, evaluation, cancellationToken);
    }

    /// <summary>Maps a supplied complete root collection without manually constructing an evaluation.</summary>
    /// <typeparam name="TRoot">CLR root type admitted against the canonical root shape.</typeparam>
    /// <param name="evaluator">Caller-owned evaluator supplying related sources and execution limits.</param>
    /// <param name="roots">Finite, non-null roots, enumerated once with cancellation between inputs.</param>
    /// <param name="selectIdentity">Selects each root's nonempty unique identity; no key convention is inferred.</param>
    /// <param name="evaluation">Optional explicit invocation identity; defaults to an operation-local identity.</param>
    /// <param name="cancellationToken">Cancels enumeration, admission, acquisition and mapping.</param>
    /// <returns>A complete owned DTO array in canonical result order. Declared joins and cardinality determine omissions.</returns>
    /// <exception cref="ArgumentNullException">A required argument, root or selected identity is null.</exception>
    /// <exception cref="ArgumentException">Root identities are empty/duplicated, root data violates its shape, or the invocation is invalid.</exception>
    /// <exception cref="NotSupportedException">A root cannot be represented as observation data.</exception>
    /// <exception cref="InvalidOperationException">Evaluation is inconclusive/failed, or strict materialization fails.</exception>
    /// <exception cref="OperationCanceledException">The invocation is canceled.</exception>
    /// <remarks>Preserves Supply's complete-root/whole-source defaults and the existing strict mapping policy.
    /// Use the evaluation-builder overload for parameters, partial evidence or explicit partitions.</remarks>
    public Task<TOutput[]> MapAsync<TRoot>(IRelationQueryEvaluator evaluator,
        IEnumerable<TRoot> roots, Func<TRoot, string> selectIdentity,
        RelationQueryEvaluationId? evaluation = null, CancellationToken cancellationToken = default) where TRoot : notnull
        => MapAsync(evaluator, SuppliedEvaluation(evaluator, roots, selectIdentity, evaluation, cancellationToken), cancellationToken);

    RelationQueryEvaluation SuppliedEvaluation<TRoot>(IRelationQueryEvaluator evaluator, IEnumerable<TRoot> roots,
        Func<TRoot, string> selectIdentity, RelationQueryEvaluationId? evaluation, CancellationToken cancellationToken)
        where TRoot : notnull
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(selectIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        return Evaluate(evaluation ?? LocalEvaluation).Supply(CancellableRoots(), selectIdentity).Build();

        IEnumerable<TRoot> CancellableRoots()
        {
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return root;
            }
        }
    }

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
