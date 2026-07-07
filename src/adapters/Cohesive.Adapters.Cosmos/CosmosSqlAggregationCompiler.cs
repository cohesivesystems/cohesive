using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.Queries;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Parameterized Cosmos SQL queries for an aggregation plan.
/// </summary>
/// <param name="Roots">Compiled query per aggregation root.</param>
public sealed record CosmosSqlAggregationPlan(IReadOnlyList<CosmosSqlAggregationRoot> Roots);

/// <summary>
/// Parameterized Cosmos SQL query for one aggregation root.
/// </summary>
/// <param name="RootName">Aggregation root name.</param>
/// <param name="Query">Parameterized Cosmos SQL query.</param>
public sealed record CosmosSqlAggregationRoot(string RootName, CosmosSqlQuery Query);

/// <summary>
/// Storage-shape options used when compiling aggregation plans to Cosmos SQL.
/// </summary>
/// <param name="RootAlias">Cosmos SQL alias used in the FROM clause.</param>
/// <param name="ValueRootExpression">Expression that points at the observation object being aggregated.</param>
/// <param name="BaseWhereClauses">Additional backend predicates that constrain the physical documents being scanned.</param>
/// <param name="Parameters">Parameters required by <paramref name="BaseWhereClauses" />.</param>
public sealed record CosmosSqlAggregationCompilerOptions(
    string RootAlias = "c",
    string ValueRootExpression = "c",
    IReadOnlyList<string>? BaseWhereClauses = null,
    IReadOnlyDictionary<string, object?>? Parameters = null
);

/// <summary>
/// Compiles relation aggregation plans to Cosmos SQL.
/// </summary>
public sealed class CosmosSqlAggregationCompiler : IAggregationCompiler<CosmosSqlAggregationPlan>
{
    internal const string DocCountFieldName = "__docCount";
    readonly CosmosSqlAggregationCompilerOptions options;

    /// <summary>
    /// Creates a Cosmos SQL aggregation compiler.
    /// </summary>
    public CosmosSqlAggregationCompiler(CosmosSqlAggregationCompilerOptions? options = null)
    {
        this.options = options ?? new();
        ArgumentException.ThrowIfNullOrWhiteSpace(this.options.RootAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(this.options.ValueRootExpression);
    }

    /// <inheritdoc />
    public AggregationBackendCapability Capabilities { get; } = new(
        Backend: "Cosmos SQL",
        Features: AggregationFeatureSet.CountIf
                  | AggregationFeatureSet.FilteredMetric
                  | AggregationFeatureSet.Histogram
                  | AggregationFeatureSet.MetricOrder,
        PredicateCapabilities: new CosmosSqlQueryCompiler().Capabilities);

    /// <inheritdoc />
    public CosmosSqlAggregationPlan Compile(AggregationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate(Capabilities);

        return new([.. plan.Roots.Select(root => CompileRoot(plan.Predicate, root))]);
    }

    CosmosSqlAggregationRoot CompileRoot(EntityPredicate? planPredicate, AggregationRoot root)
    {
        var state = new CosmosSqlQueryCompiler.CosmosSqlPredicateCompiler();
        var sql = root.Root switch
        {
            GlobalAggregationPlan global => CompileGlobal(state, planPredicate, global, root.Statistics),
            TermsGroupAggregationPlan terms => CompileGrouped(state, planPredicate, CreateTermsKey(state, terms), terms.Order, terms.Take, root.Statistics),
            HistogramAggregationPlan histogram => CompileGrouped(state, planPredicate, CreateHistogramKey(state, histogram), histogram.Order, histogram.Take, root.Statistics),
            DateHistogramAggregationPlan => throw new NotSupportedException("Cosmos SQL aggregation compilation does not support date histogram roots."),
            _ => throw new InvalidOperationException($"Unknown aggregation root type '{root.Root.GetType().Name}'.")
        };

        return new(root.Name, new(sql, MergeParameters(options.Parameters, state.Parameters)));
    }

    string CompileGlobal(
        CosmosSqlQueryCompiler.CosmosSqlPredicateCompiler state,
        EntityPredicate? predicate,
        GlobalAggregationPlan global,
        IReadOnlyList<AggregationStatistic> statistics
        )
    {
        var projection = new List<string>
        {
            $"{FormatSqlStringLiteral(global.Name)} AS key",
            $"COUNT(1) AS {DocCountFieldName}"
        };
        projection.AddRange(statistics.Select(statistic => CompileStatistic(state, statistic)));

        var builder = new StringBuilder("SELECT ");
        builder.Append(string.Join(", ", projection));
        builder.Append(" FROM ").Append(options.RootAlias);
        AppendWhere(builder, state, predicate);
        return builder.ToString();
    }

    string CompileGrouped(
        CosmosSqlQueryCompiler.CosmosSqlPredicateCompiler state,
        EntityPredicate? predicate,
        string keyExpression,
        OrderSpec? order,
        int? take,
        IReadOnlyList<AggregationStatistic> statistics
        )
    {
        var projection = new List<string>
        {
            $"{keyExpression} AS key",
            $"COUNT(1) AS {DocCountFieldName}"
        };
        projection.AddRange(statistics.Select(statistic => CompileStatistic(state, statistic)));

        var builder = new StringBuilder("SELECT ");
        builder.Append(string.Join(", ", projection));
        builder.Append(" FROM ").Append(options.RootAlias);
        AppendWhere(builder, state, predicate);
        builder.Append(" GROUP BY ").Append(keyExpression);

        if (order is not null)
        {
            builder
                .Append(" ORDER BY ")
                .Append(CompileIdentifier(order.StatisticName))
                .Append(order.Descending ? " DESC" : " ASC");
        }

        if (take is not null)
            builder.Append(" OFFSET 0 LIMIT ").Append(take.Value.ToString(CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    string CompileStatistic(CosmosSqlQueryCompiler.CosmosSqlPredicateCompiler state, AggregationStatistic aggregationStatistic)
    {
        if (string.Equals(aggregationStatistic.Name, DocCountFieldName, StringComparison.Ordinal))
            throw new AggregationPlanValidationException($"Statistic name '{DocCountFieldName}' is reserved by the Cosmos aggregation compiler.");

        var alias = CompileIdentifier(aggregationStatistic.Name);
        return aggregationStatistic switch
        {
            CountAggregationStatistic => $"COUNT(1) AS {alias}",
            SumAggregationStatistic sum => $"SUM({state.CompileScalarField(sum.Field, options.ValueRootExpression)}) AS {alias}",
            CountIfAggregationStatistic countIf => $"SUM(IIF({state.CompilePredicate(countIf.Filter, options.ValueRootExpression)}, 1, 0)) AS {alias}",
            SumIfAggregationStatistic sumIf => $"SUM(IIF({state.CompilePredicate(sumIf.Filter, options.ValueRootExpression)}, {state.CompileScalarField(sumIf.Field, options.ValueRootExpression)}, 0)) AS {alias}",
            TopHitAggregationStatistic => throw new NotSupportedException("Cosmos SQL aggregation compilation does not support top-hit statistics."),
            _ => throw new InvalidOperationException($"Unknown statistic type '{aggregationStatistic.GetType().Name}'.")
        };
    }

    string CreateTermsKey(CosmosSqlQueryCompiler.CosmosSqlPredicateCompiler state, TermsGroupAggregationPlan terms) =>
        state.CompileScalarField(terms.GroupByField, options.ValueRootExpression);

    string CreateHistogramKey(CosmosSqlQueryCompiler.CosmosSqlPredicateCompiler state, HistogramAggregationPlan histogram)
    {
        var field = state.CompileScalarField(histogram.Field, options.ValueRootExpression);
        var interval = state.AddParameter(histogram.Interval);
        if (histogram.Offset is not { } offset)
            return $"(FLOOR({field} / {interval}) * {interval})";

        var offsetParameter = state.AddParameter(offset);
        return $"((FLOOR(({field} - {offsetParameter}) / {interval}) * {interval}) + {offsetParameter})";
    }

    void AppendWhere(
        StringBuilder builder,
        CosmosSqlQueryCompiler.CosmosSqlPredicateCompiler state,
        EntityPredicate? predicate
        )
    {
        List<string> clauses = [];
        if (options.BaseWhereClauses is not null)
            clauses.AddRange(options.BaseWhereClauses.Where(static clause => !string.IsNullOrWhiteSpace(clause)));

        if (predicate is not null)
            clauses.Add(state.CompilePredicate(predicate, options.ValueRootExpression));

        if (clauses.Count == 0)
            return;

        builder.Append(" WHERE ");
        builder.Append(clauses.Count == 1
            ? clauses[0]
            : string.Join(" AND ", clauses.Select(static clause => $"({clause})")));
    }

    static IReadOnlyDictionary<string, object?> MergeParameters(
        IReadOnlyDictionary<string, object?>? baseParameters,
        IReadOnlyDictionary<string, object?> compiledParameters
        )
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        if (baseParameters is not null)
        {
            foreach (var (name, value) in baseParameters)
                result.Add(name, value);
        }

        foreach (var (name, value) in compiledParameters)
        {
            if (result.ContainsKey(name))
                throw new InvalidOperationException($"Cosmos SQL aggregation parameter '{name}' is defined more than once.");

            result[name] = value;
        }

        return result;
    }

    static string CompileIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new AggregationPlanValidationException("Cosmos SQL aggregation aliases must not be empty.");

        if (!IsIdentifierStart(identifier[0]) || identifier.Skip(1).Any(static ch => !IsIdentifierPart(ch)))
            throw new AggregationPlanValidationException($"Cosmos SQL aggregation alias '{identifier}' must be a simple identifier containing only letters, digits, and underscores.");

        return identifier;

        static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';
        static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
    }

    static string FormatSqlStringLiteral(string value) =>
        JsonSerializer.Serialize(value);
}

/// <summary>
/// Reads Cosmos SQL aggregation rows into relation aggregation results.
/// </summary>
public static class CosmosSqlAggregationResultReader
{
    /// <summary>
    /// Reads result rows keyed by aggregation root name.
    /// </summary>
    public static IReadOnlyDictionary<string, AggregationResult> Read(
        IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> rowsByRoot,
        AggregationPlan plan
        )
    {
        ArgumentNullException.ThrowIfNull(rowsByRoot);
        ArgumentNullException.ThrowIfNull(plan);

        Dictionary<string, AggregationResult> results = new(StringComparer.Ordinal);
        foreach (var root in plan.Roots)
        {
            rowsByRoot.TryGetValue(root.Name, out var rows);
            rows ??= [];
            results[root.Name] = root.Root switch
            {
                GlobalAggregationPlan global => new SingletonAggregationResult(ReadSingleton(global, rows, root.Statistics)),
                TermsGroupAggregationPlan or HistogramAggregationPlan => new BucketedAggregationResult([.. rows.Select(row => ReadRow(row, root.Statistics))]),
                DateHistogramAggregationPlan => throw new NotSupportedException("Cosmos SQL aggregation result reading does not support date histogram roots."),
                _ => throw new InvalidOperationException($"Unknown aggregation root type '{root.Root.GetType().Name}'.")
            };
        }

        return results;
    }

    static AggregationResultRow ReadSingleton(
        GlobalAggregationPlan global,
        IReadOnlyList<JsonElement> rows,
        IReadOnlyList<AggregationStatistic> statistics
        )
    {
        if (rows.Count == 0)
        {
            return new(
                Key: global.Name,
                DocCount: 0,
                Statistics: statistics.ToDictionary(static statistic => statistic.Name, static _ => (double?)null, StringComparer.Ordinal),
                Samples: new Dictionary<string, JsonElement?>(StringComparer.Ordinal));
        }

        return ReadRow(rows[0], statistics);
    }

    static AggregationResultRow ReadRow(JsonElement row, IReadOnlyList<AggregationStatistic> statistics)
    {
        var key = row.TryGetProperty("key", out var keyElement)
            ? FormatKey(keyElement)
            : string.Empty;
        var docCount = row.TryGetProperty(CosmosSqlAggregationCompiler.DocCountFieldName, out var docCountElement)
            ? docCountElement.GetInt64()
            : 0L;

        Dictionary<string, double?> values = new(StringComparer.Ordinal);
        foreach (var statistic in statistics)
        {
            values[statistic.Name] = row.TryGetProperty(statistic.Name, out var value)
                ? ReadNullableDouble(value)
                : null;
        }

        return new(key, docCount, values, new Dictionary<string, JsonElement?>(StringComparer.Ordinal));
    }

    static double? ReadNullableDouble(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.GetDouble();

    static string FormatKey(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText()
    };
}
