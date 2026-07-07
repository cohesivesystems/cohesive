using Cohesive.Model;
using Cohesive.Relations.Queries;
using global::Elastic.Clients.Elasticsearch;
using global::Elastic.Clients.Elasticsearch.QueryDsl;

namespace Cohesive.Adapters.Elastic;

using ElasticQuery = global::Elastic.Clients.Elasticsearch.QueryDsl.Query;

/// <summary>
/// Compiles relation predicates to Elasticsearch query DSL.
/// </summary>
public sealed class ElasticQueryCompiler : IQueryCompiler<ElasticQuery>
{
    /// <inheritdoc />
    public QueryCapabilitySet Capabilities { get; } = new(
        QueryCapability.Equality
        | QueryCapability.Prefix
        | QueryCapability.Suffix
        | QueryCapability.Contains
        | QueryCapability.FullText
        | QueryCapability.Exists
        | QueryCapability.NumberRange
        | QueryCapability.DateRange
        | QueryCapability.SetMembership
        | QueryCapability.NestedAny
        | QueryCapability.ScopedFields
        | QueryCapability.Negation
        | QueryCapability.CaseInsensitiveStringComparison);

    /// <inheritdoc />
    public ElasticQuery Compile(EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Capabilities.EnsureSupports(
            QueryCapabilityInspector.GetRequiredCapabilities(predicate).Value,
            operation: $"compile predicate with '{nameof(ElasticQueryCompiler)}'");

        return CompilePredicate(predicate, fieldPrefix: null);
    }

    internal ElasticQuery CompilePredicate(EntityPredicate predicate, string? fieldPrefix)
    {
        var normalized = predicate.Predicate.Normalize();
        if (predicate.Scope is not { } scope)
            return CompileFieldPredicate(normalized, fieldPrefix);

        var nestedPath = ComposeFieldName(fieldPrefix, scope);
        return new NestedQuery
        {
            Path = new Field(nestedPath),
            Query = CompileFieldPredicate(normalized, nestedPath)
        };
    }

    ElasticQuery CompileFieldPredicate(BoolExpr<FieldPredicate> expr, string? fieldPrefix) => expr switch
    {
        Atom<FieldPredicate> atom => CompileValuePredicate(
            fieldName: ComposeFieldName(fieldPrefix, atom.Term.Field),
            expr: atom.Term.Predicate.Normalize()
            ),
        And<FieldPredicate> conjunction => new BoolQuery
        {
            Must = [.. conjunction.Terms.Select(term => CompileFieldPredicate(term, fieldPrefix))]
        },
        Or<FieldPredicate> disjunction => new BoolQuery
        {
            Should = [.. disjunction.Terms.Select(term => CompileFieldPredicate(term, fieldPrefix))],
            MinimumShouldMatch = 1
        },
        Not<FieldPredicate> negation => new BoolQuery
        {
            MustNot = [CompileFieldPredicate(negation.Term, fieldPrefix)]
        },
        _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
    };

    ElasticQuery CompileValuePredicate(string fieldName, BoolExpr<ValuePredicate> expr) => expr switch
    {
        Atom<ValuePredicate> atom => CompileValueAtom(fieldName, atom.Term),
        And<ValuePredicate> conjunction => new BoolQuery
        {
            Must = [.. conjunction.Terms.Select(term => CompileValuePredicate(fieldName, term))]
        },
        Or<ValuePredicate> disjunction => new BoolQuery
        {
            Should = [.. disjunction.Terms.Select(term => CompileValuePredicate(fieldName, term))],
            MinimumShouldMatch = 1
        },
        Not<ValuePredicate> negation => new BoolQuery
        {
            MustNot = [CompileValuePredicate(fieldName, negation.Term)]
        },
        _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
    };

    ElasticQuery CompileValueAtom(string fieldName, ValuePredicate predicate) => predicate switch
    {
        ExactValuePredicate exact => BuildEquals(fieldName, exact.Value, exact.CaseSensitive),
        BoolValuePredicate flag => BuildEquals(fieldName, flag.Value),
        IntValuePredicate value => BuildEquals(fieldName, value.Value),
        LongValuePredicate value => BuildEquals(fieldName, value.Value),
        DoubleValuePredicate value => BuildEquals(fieldName, value.Value),
        DecimalValuePredicate value => BuildEquals(fieldName, value.Value),
        DateValuePredicate value => BuildEquals(fieldName, value.Value),
        PrefixValuePredicate prefix => new PrefixQuery(new Field(fieldName), prefix.Prefix)
        {
            CaseInsensitive = ToCaseInsensitiveFlag(prefix.CaseSensitive)
        },
        SuffixValuePredicate suffix => new WildcardQuery(new Field(fieldName))
        {
            Value = $"*{EscapeWildcard(suffix.Suffix)}",
            CaseInsensitive = ToCaseInsensitiveFlag(suffix.CaseSensitive)
        },
        ContainsValuePredicate contains => new WildcardQuery(new Field(fieldName))
        {
            Value = $"*{EscapeWildcard(contains.Value)}*",
            CaseInsensitive = ToCaseInsensitiveFlag(contains.CaseSensitive)
        },
        FullTextValuePredicate fullText => new MatchQuery(new Field(fieldName), fullText.Text),
        ExistsValuePredicate => new ExistsQuery { Field = new Field(fieldName) },
        DateRangeValuePredicate range => BuildDateRange(fieldName, range),
        NumberRangeValuePredicate range => BuildNumberRange(fieldName, range),
        InValuePredicate set => BuildSetMembership(fieldName, set.Values),
        AnyValuePredicate any => CompileValuePredicate(fieldName, any.Predicate.Normalize()),
        AnyFieldPredicate any => new NestedQuery
        {
            Path = new Field(fieldName),
            Query = CompileFieldPredicate(any.Predicate.Normalize(), fieldName)
        },
        GeoDistanceValuePredicate => throw new NotSupportedException("Elasticsearch query compilation does not yet support geo-distance predicates."),
        _ => throw new InvalidOperationException($"Unsupported value-predicate type '{predicate.GetType().Name}'.")
    };

    static ElasticQuery BuildEquals(string fieldName, object value, bool caseSensitive = true) =>
        new TermQuery(new Field(fieldName), value: ToFieldValue(value))
        {
            CaseInsensitive = value is string && !caseSensitive ? true : null
        };

    static bool? ToCaseInsensitiveFlag(bool caseSensitive) =>
        caseSensitive ? null : true;

    static ElasticQuery BuildSetMembership(string fieldName, IReadOnlyCollection<object> values)
    {
        if (values.Count == 0)
            return new MatchNoneQuery();

        return new BoolQuery
        {
            Should = [.. values.Select(value => BuildEquals(fieldName, value))],
            MinimumShouldMatch = 1
        };
    }

    static ElasticQuery BuildDateRange(string fieldName, DateRangeValuePredicate range)
    {
        var query = new DateRangeQuery(new(name: fieldName));
        if (range.Start is { } start)
        {
            if (range.StartExclusive == true)
                query.Gt = DateMath.Anchored(start.UtcDateTime);
            else
                query.Gte = DateMath.Anchored(start.UtcDateTime);
        }

        if (range.End is { } end)
        {
            if (range.EndExclusive == true)
                query.Lt = DateMath.Anchored(end.UtcDateTime);
            else
                query.Lte = DateMath.Anchored(end.UtcDateTime);
        }

        return query;
    }

    static ElasticQuery BuildNumberRange(string fieldName, NumberRangeValuePredicate range)
    {
        var query = new NumberRangeQuery(new Field(fieldName));
        if (range.Start is { } start)
        {
            if (range.StartExclusive == true)
                query.Gt = start;
            else
                query.Gte = start;
        }

        if (range.End is { } end)
        {
            if (range.EndExclusive == true)
                query.Lt = end;
            else
                query.Lte = end;
        }

        return query;
    }

    internal static string ComposeFieldName(string? prefix, FieldPath field)
    {
        var fieldName = ToElasticFieldName(field);
        return string.IsNullOrWhiteSpace(prefix) ? fieldName : $"{prefix}.{fieldName}";
    }

    internal static string ToElasticFieldName(FieldPath field) =>
        string.Join(
            ".",
            field.Segments
                .Where(static segment => segment.Kind == SegmentKind.Field)
                .Select(static segment => segment.Segment)
                .Where(static segment => !string.IsNullOrWhiteSpace(segment)));

    static FieldValue ToFieldValue(object value) => value switch
    {
        string text => FieldValue.String(text),
        bool flag => FieldValue.Boolean(flag),
        byte number => FieldValue.Long(number),
        short number => FieldValue.Long(number),
        int number => FieldValue.Long(number),
        long number => FieldValue.Long(number),
        float number => FieldValue.Double(number),
        double number => FieldValue.Double(number),
        decimal number => FieldValue.Double((double)number),
        DateTime date => FieldValue.String(date.ToString("O")),
        DateTimeOffset date => FieldValue.String(date.ToString("O")),
        Enum enumValue => FieldValue.String(enumValue.ToString()),
        Guid guidValue => FieldValue.String(guidValue.ToString()),
        _ => FieldValue.String(value.ToString() ?? string.Empty)
    };

    static string EscapeWildcard(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("?", "\\?", StringComparison.Ordinal);
}
