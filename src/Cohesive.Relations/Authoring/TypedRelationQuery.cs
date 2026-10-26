using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Authoring;

/// <summary>A typed invocation and local result projection attached to one canonical row query.</summary>
/// <typeparam name="TInput">Type of the single canonical invocation parameter.</typeparam>
/// <typeparam name="TResult">Public application result, including its explicit empty-result policy.</typeparam>
/// <remarks>The captured compilation request is the portable query authority. The result callback is a local CLR
/// presentation projection unless produced from canonical nested-result assembly metadata. No compiler, backend or runtime is retained here.</remarks>
public sealed class RelationQuery<TInput, TResult>
{
    readonly Func<ImmutableArray<ObservationValue>, TResult> project;

    internal RelationQuery(RelationQueryCompilationRequest request, QueryParameterId parameter,
        Func<ImmutableArray<ObservationValue>, TResult> project)
    {
        CompilationRequest = request;
        Parameter = parameter;
        this.project = project;
    }

    /// <summary>Exact canonical query, shapes and relationships captured at authoring completion.</summary>
    public RelationQueryCompilationRequest CompilationRequest { get; }
    /// <summary>Canonical identity of the typed invocation parameter.</summary>
    public QueryParameterId Parameter { get; }

    /// <summary>Projects complete canonical rows into the declared local result.</summary>
    /// <param name="rows">Complete rows from this query; adapters retain missing-field semantics until this boundary.</param>
    /// <returns>The declared application result.</returns>
    /// <remarks>The callback must be safe for concurrent invocations and must not perform backend work.</remarks>
    public TResult AssembleResult(ImmutableArray<ObservationValue> rows) => project(rows);

    /// <summary>Projects a complete composed evaluation without discarding or replacing its retained evidence.</summary>
    /// <param name="outcome">Evaluation of this exact compilation request; the caller retains the full outcome.</param>
    /// <param name="maximumRows">Maximum complete flat rows before typed assembly.</param>
    /// <param name="maximumBytes">Maximum decoded scalar payload bytes across rows: UTF-8 strings, raw binary,
    /// one-byte booleans, eight-byte numeric/time scalars, sixteen-byte decimal/date-time-offset; excludes object overhead.</param>
    /// <returns>The same application result produced from native complete rows.</returns>
    /// <exception cref="ArgumentNullException">The outcome is null.</exception>
    /// <exception cref="ArgumentException">The outcome belongs to another query snapshot.</exception>
    /// <exception cref="InvalidOperationException">Execution failed, was incomplete or suppressed, or returned unresolved rows.</exception>
    public TResult AssembleResult(RelationQueryEvaluationOutcome outcome, int maximumRows = int.MaxValue, long maximumBytes = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!ReferenceEquals(outcome.Evaluation.Compilation, CompilationRequest))
            throw new ArgumentException("The outcome must belong to this exact query compilation request.", nameof(outcome));
        if (!outcome.IsSuccessful || outcome.Result is not { QueryResults.Length: 1 } result
            || result.QueryResults[0].State != RelationQueryExecutionOutputState.Complete
            || result.QueryResults[0].Rows.Any(row => !row.IsComplete))
            throw new InvalidOperationException("Typed projection requires a complete, unsuppressed query outcome.");
        var rows = result.QueryResults[0].Rows;
        if (rows.Length > maximumRows) throw new InvalidOperationException("Composed query exceeded its complete-result row bound.");
        if (maximumBytes != long.MaxValue)
        {
            var remaining = maximumBytes;
            foreach (var row in rows) Consume(row.Value, ref remaining);
        }
        return project([.. rows.Select(row => row.Value)]);
    }

    static void Consume(ObservationValue value, ref long remaining)
    {
        if (value.Kind == ObservationValueKind.Object)
        {
            foreach (var field in value.Fields!) Consume(field.Value, ref remaining);
            return;
        }
        if (value.Kind == ObservationValueKind.Array)
        {
            foreach (var item in value.Array) Consume(item, ref remaining);
            return;
        }
        long size = value.Kind switch
        {
            ObservationValueKind.Undefined or ObservationValueKind.Null => 0,
            ObservationValueKind.String => System.Text.Encoding.UTF8.GetByteCount(value.GetRequiredString()),
            ObservationValueKind.Bytes => value.Bytes.Length,
            ObservationValueKind.Bool => 1,
            ObservationValueKind.Decimal or ObservationValueKind.DateTimeOffset => 16,
            ObservationValueKind.Int64 or ObservationValueKind.Double or ObservationValueKind.DateOnly
                or ObservationValueKind.TimeOnly or ObservationValueKind.TimeSpan => 8,
            _ => throw new InvalidOperationException("Unknown observation kind in query result.")
        };
        if (size > remaining) throw new InvalidOperationException("Composed query exceeded its complete-result byte bound.");
        remaining -= size;
    }

}

public sealed partial class RelationQueryExpressionAuthoring
{
    /// <summary>Captures a row query with a typed input and public result, without compiling or choosing a backend.</summary>
    /// <typeparam name="TNode">Canonical output node.</typeparam>
    /// <typeparam name="TRow">Inferred flat row projection type; may be anonymous.</typeparam>
    /// <typeparam name="TInput">Canonical invocation parameter type.</typeparam>
    /// <typeparam name="TResult">Application result type.</typeparam>
    /// <param name="id">Stable query identity.</param>
    /// <param name="name">Human-readable query name.</param>
    /// <param name="rows">Typed projection produced by this session.</param>
    /// <param name="parameter">The query's single invocation parameter, owned by this session.</param>
    /// <param name="result">Local presentation mapping of complete typed rows; defines empty and collection behavior.</param>
    /// <returns>An immutable typed handle; preparation is delegated to the selected runtime adapter.</returns>
    /// <exception cref="ArgumentException">Handles have a different owner or the query has other parameters.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public RelationQuery<TInput, TResult> BuildQuery<TNode, TRow, TInput, TResult>(
        QueryId id, QueryName name, RelationQueryExpressionBoundNode<TNode, TRow> rows,
        RelationQueryExpressionParameter<TInput> parameter, Func<IReadOnlyList<TRow>, TResult> result)
        where TNode : LogicalQueryNode where TRow : notnull
    {
        ArgumentNullException.ThrowIfNull(result);
        return BuildProjectedQuery(id, name, rows, parameter, result);
    }

    internal RelationQuery<TInput, TRow[]> BuildArrayQuery<TNode, TRow, TInput>(
        QueryId id, QueryName name, RelationQueryExpressionBoundNode<TNode, TRow> rows,
        RelationQueryExpressionParameter<TInput> parameter)
        where TNode : LogicalQueryNode where TRow : notnull =>
        BuildProjectedQuery(id, name, rows, parameter, static (TRow[] values) => values);

    RelationQuery<TInput, TResult> BuildProjectedQuery<TNode, TRow, TInput, TResult>(
        QueryId id, QueryName name, RelationQueryExpressionBoundNode<TNode, TRow> rows,
        RelationQueryExpressionParameter<TInput> parameter, Func<TRow[], TResult> result)
        where TNode : LogicalQueryNode where TRow : notnull
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!ReferenceEquals(parameter.Owner, this))
            throw new ArgumentException("The invocation parameter belongs to another query session.", nameof(parameter));
        var authored = BuildQuery(id, name, Rows(rows));
        var request = new RelationQueryCompilationRequest(authored.CreateDocument(), ShapeDocuments, CreateRelationshipCatalogDocument());
        if (request.DefinitionDocument.Definition.Body.Parameters.Length != 1
            || request.DefinitionDocument.Definition.Body.Parameters[0].Id != parameter.Id)
            throw new ArgumentException("The typed row query requires exactly one invocation parameter.", nameof(parameter));
        var materialize = ObservationMaterializer.PrepareJsonValue<TRow>();
        return new(request, parameter.Id, values =>
        {
            var typed = new TRow[values.Length];
            for (var index = 0; index < typed.Length; index++)
                typed[index] = materialize(values[index])!;
            return result(typed);
        });
    }

    /// <summary>Filters a focused typed branch, retaining its binding for subsequent traversal or projection.</summary>
    /// <typeparam name="TNode">Input node type.</typeparam>
    /// <typeparam name="T">Focused row type.</typeparam>
    /// <param name="input">Branch owned by this session.</param>
    /// <param name="predicate">Canonical predicate over the focused row.</param>
    /// <returns>A filtered branch retaining its typed binding.</returns>
    /// <exception cref="ArgumentException">The branch belongs to another session.</exception>
    public RelationQueryExpressionBoundNode<FilterQueryNode, T> Where<TNode, T>(
        RelationQueryExpressionBoundNode<TNode, T> input, Expression<Func<T, bool>> predicate)
        where TNode : LogicalQueryNode where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        var filtered = Filter(input.Node, predicate, input.Binding);
        return new(filtered, input.Binding, input.RelationRoot);
    }

    /// <summary>
    /// Limits a focused typed branch to its first <paramref name="count"/> rows, retaining its binding.
    /// </summary>
    /// <remarks>
    /// Lowers to a canonical offset page with no offset, so callers do not construct page definitions directly.
    /// </remarks>
    /// <typeparam name="TNode">Input node type.</typeparam>
    /// <typeparam name="T">Focused row type.</typeparam>
    /// <param name="input">Branch owned by this session.</param>
    /// <param name="count">Maximum number of rows the branch yields; must be positive.</param>
    /// <returns>A paged branch retaining its typed binding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="ArgumentException">The branch belongs to another session.</exception>
    public RelationQueryExpressionBoundNode<PageQueryNode, T> Take<TNode, T>(
        RelationQueryExpressionBoundNode<TNode, T> input, int count)
        where TNode : LogicalQueryNode where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        var paged = Page(input.Node, new OffsetPageDefinition(limit: count));
        return new(paged, input.Binding, input.RelationRoot);
    }
}
