using System.Globalization;
using System.Linq.Expressions;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Mapping;

/// <summary>Authors and prepares reusable DTO projections and composed canonical relations.</summary>
public static class RelationDtoMapping
{
    /// <summary>Lowers one projection and prepares its single-source, one-output-per-input mapping.</summary>
    /// <typeparam name="TSource">Non-null CLR source type.</typeparam>
    /// <typeparam name="TOutput">Non-null CLR DTO type.</typeparam>
    /// <param name="projection">Inspected expression lowered to portable IR; never compiled as a CLR callback.</param>
    /// <param name="maximumRows">Positive complete batch bound, checked while enumerating inputs.</param>
    /// <returns>A reusable mapping with exact inspectable compilation inputs, plan and DTO binding decisions.</returns>
    /// <exception cref="ArgumentNullException">The projection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The row bound is not positive.</exception>
    /// <exception cref="ArgumentException">A CLR source or result type is not an admissible object shape.</exception>
    /// <exception cref="RelationQueryPreparationException">The lowered relation or DTO binding cannot be compiled; original diagnostics are retained.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The expression cannot be lowered exactly.</exception>
    /// <remarks>No source values are read and no I/O occurs. Source identities are invocation-local ordinal
    /// occurrences, not inferred entity keys. Output DTOs need no identity member. Output conversions retain the
    /// existing scalar DTO mapper boundary; nested/collection DTO members are rejected with its diagnostics. Captures and arbitrary
    /// executable dependencies follow the existing expression lowerer's fail-closed rules.</remarks>
    public static RelationDtoMapping<TSource, TOutput> Create<TSource, TOutput>(
        Expression<Func<TSource, TOutput>> projection,
        int maximumRows = (int)RelationQueryEvaluator.DefaultMaximumRootRows)
        where TSource : notnull where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        var author = RelationQuery.Expression();
        var source = author.Source<TSource>();
        var relation = author.Project(source, projection).BuildRelation();
        return new(Prepare<TOutput>(author, relation), maximumRows);
    }

    /// <summary>Prepares a composed relation while keeping its root and output cardinality explicit.</summary>
    /// <typeparam name="TRoot">Root source type.</typeparam>
    /// <typeparam name="TNode">Output node type.</typeparam>
    /// <typeparam name="TOutput">DTO type.</typeparam>
    /// <param name="root">Source defining relation-root occurrences.</param>
    /// <param name="output">Composed output from the same authoring session.</param>
    /// <param name="mode">Required explicit per-root cardinality; enforced by canonical interpretation.</param>
    /// <returns>A prepared mapping usable with an explicitly configured evaluator.</returns>
    /// <exception cref="ArgumentNullException">A required handle is null.</exception>
    /// <exception cref="ArgumentException">Handles, output shape or relation declaration are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The output mode is unsupported.</exception>
    /// <exception cref="RelationQueryPreparationException">Semantic or DTO compilation fails; original diagnostics are retained.</exception>
    public static RelationDtoMapping<TOutput> Create<TRoot, TNode, TOutput>(
        RelationQueryExpressionBoundNode<SourceQueryNode, TRoot> root,
        RelationQueryExpressionBoundNode<TNode, TOutput> output, RelationOutputMode mode)
        where TRoot : notnull where TNode : LogicalQueryNode where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(output);
        var author = root.Binding.Owner;
        return Prepare<TOutput>(author, author.BuildRelation(root, output, mode));
    }

    static RelationDtoMapping<TOutput> Prepare<TOutput>(RelationQueryExpressionAuthoring author,
        RelationQueryAuthoringResult<RelationDefinition> relation) where TOutput : notnull
    {
        var request = new RelationQueryCompilationRequest(relation.CreateDocument(), author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument());
        var compilation = RelationQueryEvaluator.CompileRequest(request);
        if (!compilation.IsSuccessful)
            throw new RelationQueryPreparationException("DTO projection", compilation,
                detail: string.Join(Environment.NewLine, compilation.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        var materialization = RelationDtoMapperCompiler.Default.Compile<TOutput>(compilation.Plan!);
        if (!materialization.IsSuccessful)
            throw new RelationQueryPreparationException("DTO materialization", compilation,
                detail: string.Join(Environment.NewLine, materialization.Diagnostics.Select(d => $"{d.Code}: {d.Message}")),
                dtoDiagnostics: materialization.Diagnostics);
        return new(compilation, materialization.Mapper!, relation.Provenance, author.SnapshotSourceTypes(compilation.Plan!));
    }
}

/// <summary>A prepared local projection retaining canonical compilation, validation and execution semantics.</summary>
/// <typeparam name="TSource">Non-null CLR source type.</typeparam>
/// <typeparam name="TOutput">Non-null CLR DTO type.</typeparam>
/// <remarks>Safe for concurrent invocations. Each invocation owns its admitted observations and DTOs.
/// Source enumeration and getters run during admission; callers must not concurrently mutate their input.
/// This facade performs the full supplied-only pipeline, not a direct CLR delegate conversion.</remarks>
public sealed class RelationDtoMapping<TSource, TOutput>
    where TSource : notnull where TOutput : notnull
{
    readonly RelationQueryEvaluator evaluator;
    readonly RelationDtoMapping<TOutput> prepared;

    internal RelationDtoMapping(RelationDtoMapping<TOutput> prepared, int maximumRows)
    {
        this.prepared = prepared;
        MaximumRows = maximumRows;
        evaluator = RelationQueryEvaluator.CreateSuppliedOnly(maximumRootRows: maximumRows);
    }

    /// <summary>Exact portable definition and shape snapshots, suitable for serialization and other interpretations.</summary>
    public RelationQueryCompilationRequest CompilationRequest => prepared.CompilationRequest;
    /// <summary>Reusable static plan and its structured diagnostics.</summary>
    public RelationQueryCompilationResult Compilation => prepared.Compilation;
    /// <summary>Prepared DTO mapper, including binding decisions and provenance-preserving result APIs.</summary>
    public CompiledRelationDtoMapper<TOutput> Mapper => prepared.Mapper;
    /// <summary>Original expression-lowering decisions and attributable source references.</summary>
    public RelationQueryAuthoringManifest AuthoringProvenance => prepared.AuthoringProvenance;
    /// <summary>Maximum complete input count admitted per invocation.</summary>
    public int MaximumRows { get; }

    /// <summary>Admits and maps exactly one source value.</summary>
    /// <param name="source">Caller-owned non-null source; its getters are read during admission.</param>
    /// <param name="cancellationToken">Cancels admission, evaluation and materialization.</param>
    /// <returns>One fully materialized owned DTO; no partial result is returned.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">Input data violates its inferred or configured shape.</exception>
    /// <exception cref="NotSupportedException">Input data cannot be represented as observation data.</exception>
    /// <exception cref="InvalidOperationException">Evaluation, materialization or the one-per-input guarantee fails.</exception>
    /// <exception cref="OperationCanceledException">The invocation is canceled.</exception>
    public async Task<TOutput> MapAsync(TSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = await MapManyAsync([source], cancellationToken).ConfigureAwait(false);
        return values[0];
    }

    /// <summary>Admits and maps a bounded batch in input order, preserving repeated values.</summary>
    /// <param name="sources">Finite caller-owned inputs, enumerated once; elements must not be null.</param>
    /// <param name="cancellationToken">Cancels enumeration, admission, evaluation and materialization.</param>
    /// <returns>Owned DTO array in input order; empty input returns an empty array. No partial batch escapes.</returns>
    /// <exception cref="ArgumentNullException">The input sequence or one of its elements is null.</exception>
    /// <exception cref="ArgumentException">The row bound is exceeded or input data violates its shape.</exception>
    /// <exception cref="NotSupportedException">Input data cannot be represented as observation data.</exception>
    /// <exception cref="InvalidOperationException">Evaluation, materialization or the one-per-input guarantee fails.</exception>
    /// <exception cref="OperationCanceledException">The invocation is canceled.</exception>
    /// <remarks>The default evaluation identity is local to this operation. Use <see cref="EvaluateAsync"/>
    /// with an explicit identity for attributable execution and retained phase diagnostics/provenance.</remarks>
    public async Task<TOutput[]> MapManyAsync(IEnumerable<TSource> sources, CancellationToken cancellationToken = default)
    {
        var outcome = await EvaluateAsync(sources, RelationDtoMapping<TOutput>.LocalEvaluation, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccessful)
            throw new InvalidOperationException($"DTO projection evaluation failed with status {outcome.Status}; use EvaluateAsync for phase diagnostics.");
        var mapped = Mapper.Map(outcome.PhysicalExecution!, cancellationToken: cancellationToken);
        var count = outcome.Evaluation.SuppliedRoots!.Observations.Length;
        if (!mapped.IsSuccessful || mapped.Rows.Length != count)
            throw new InvalidOperationException($"DTO projection did not produce one complete result per input. {string.Join(Environment.NewLine, mapped.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
        var outputs = new TOutput[count];
        var assigned = new bool[count];
        foreach (var row in mapped.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = row.Source.Root?.ObservationIdentity;
            if (!int.TryParse(identity, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || (uint)index >= (uint)count || identity != OrdinalIdentity(index) || assigned[index])
                throw new InvalidOperationException("DTO projection lost its input occurrence attribution.");
            outputs[index] = row.Value;
            assigned[index] = true;
        }
        return outputs;
    }

    /// <summary>Evaluates source values while retaining every canonical phase artifact and source occurrence.</summary>
    /// <param name="sources">Finite non-null inputs, enumerated once and bounded by <see cref="MaximumRows"/>.</param>
    /// <param name="evaluation">Explicit caller-owned invocation identity; source ordinals are scoped to it.</param>
    /// <param name="cancellationToken">Cancels enumeration, admission and execution.</param>
    /// <returns>The exact evaluation outcome, including structured failures. Use <see cref="Mapper"/> on its
    /// physical execution to obtain DTOs with source provenance rather than the value-only convenience result.</returns>
    /// <exception cref="ArgumentNullException">The input sequence or an element is null.</exception>
    /// <exception cref="ArgumentException">The row bound is exceeded, the identity is invalid, or input violates its shape.</exception>
    /// <exception cref="NotSupportedException">Input cannot be represented as observation data.</exception>
    /// <exception cref="OperationCanceledException">The invocation is canceled.</exception>
    public async Task<RelationQueryEvaluationOutcome> EvaluateAsync(IEnumerable<TSource> sources,
        RelationQueryEvaluationId evaluation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        var ordinal = 0;
        var request = new RelationQueryEvaluationBuilder(CompilationRequest, evaluation)
            .Supply(BoundedSources(), _ => OrdinalIdentity(ordinal++))
            .Build();
        return await evaluator.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);

        IEnumerable<TSource> BoundedSources()
        {
            var count = 0;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count == MaximumRows)
                    throw new ArgumentException($"DTO mapping exceeds its complete input bound of {MaximumRows} rows.", nameof(sources));
                count++;
                yield return source;
            }
        }
    }

    // Fixed-width decimal ordering matches the canonical supplied-root order for every supported Int32 index.
    static string OrdinalIdentity(int ordinal) => ordinal.ToString("D10", CultureInfo.InvariantCulture);
}
