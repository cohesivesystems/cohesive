using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.IR;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using ElasticQuery = Elastic.Clients.Elasticsearch.QueryDsl.Query;
using ElasticSortOrder = Elastic.Clients.Elasticsearch.SortOrder;

namespace Cohesive.Adapters.Elastic;

/// <summary>Physical Elasticsearch query clause emitted by canonical lowering or an extension strategy.</summary>
public enum ElasticQueryTemplateKind
{
    /// <summary>Matches every indexed document.</summary>
    MatchAll = 0,

    /// <summary>Matches one exact indexed term.</summary>
    Term = 1,

    /// <summary>Matches one bounded scalar range.</summary>
    Range = 2,

    /// <summary>Matches documents containing an indexed field value.</summary>
    Exists = 3,

    /// <summary>Matches a wildcard pattern against an exact indexed value.</summary>
    Wildcard = 4,

    /// <summary>Matches an indexed term prefix.</summary>
    Prefix = 5,

    /// <summary>
    /// Combines filter-context clauses with query-context disjunction and negation clauses; enclosing placement
    /// determines whether the complete Boolean clause contributes to scoring.
    /// </summary>
    Boolean = 6
}

/// <summary>Source of a reusable Elasticsearch query-template value.</summary>
public enum ElasticQueryValueSourceKind
{
    /// <summary>The value is embedded in the compiled template.</summary>
    Constant = 0,

    /// <summary>The value is supplied by one canonical invocation parameter.</summary>
    Parameter = 1
}

/// <summary>Deterministic physical transformation applied when a template value is bound.</summary>
public enum ElasticQueryValueTransform
{
    /// <summary>Retains the canonical scalar value unchanged.</summary>
    None = 0,

    /// <summary>Escapes Elasticsearch wildcard metacharacters and prefixes the value with <c>*</c>.</summary>
    WildcardSuffix = 1,

    /// <summary>Reverses a well-formed Unicode scalar sequence for a precomputed reversed-value field.</summary>
    ReverseUnicodeScalars = 2
}

/// <summary>Reusable constant or canonical-parameter value used by an Elasticsearch request template.</summary>
public sealed class ElasticQueryValueTemplate
{
    ElasticQueryValueTemplate(
        ElasticQueryValueSourceKind sourceKind,
        ObservationValue constant,
        QueryParameterId? parameter,
        ElasticQueryValueTransform transform)
    {
        if (!Enum.IsDefined(sourceKind))
            throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, "Unsupported Elasticsearch value source.");
        if (!Enum.IsDefined(transform))
            throw new ArgumentOutOfRangeException(nameof(transform), transform, "Unsupported Elasticsearch value transform.");
        if (sourceKind == ElasticQueryValueSourceKind.Constant != (parameter is null))
            throw new ArgumentException("Only a parameter value template can retain a parameter identity.", nameof(parameter));
        if (parameter is { } parameterId && string.IsNullOrWhiteSpace(parameterId.Value))
            throw new ArgumentException("An Elasticsearch value-template parameter cannot be empty.", nameof(parameter));
        if (sourceKind == ElasticQueryValueSourceKind.Constant && !IsSupportedScalar(constant))
            throw new ArgumentException("An Elasticsearch query-template constant must be a supported scalar value.", nameof(constant));

        SourceKind = sourceKind;
        Constant = constant;
        Parameter = parameter;
        Transform = transform;
    }

    /// <summary>Whether the value is constant or supplied by an invocation parameter.</summary>
    public ElasticQueryValueSourceKind SourceKind { get; }

    /// <summary>Canonical constant value when <see cref="SourceKind"/> is <see cref="ElasticQueryValueSourceKind.Constant"/>.</summary>
    public ObservationValue Constant { get; }

    /// <summary>Canonical invocation parameter, or <see langword="null"/> for a constant.</summary>
    public QueryParameterId? Parameter { get; }

    /// <summary>Deterministic physical transformation applied after resolving the canonical value.</summary>
    public ElasticQueryValueTransform Transform { get; }

    /// <summary>Creates a constant Elasticsearch query-template value.</summary>
    /// <param name="value">Supported canonical scalar value.</param>
    /// <param name="transform">Deterministic target transformation.</param>
    /// <returns>A reusable constant-value template.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a supported scalar.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transform"/> is unsupported.</exception>
    public static ElasticQueryValueTemplate FromConstant(
        ObservationValue value,
        ElasticQueryValueTransform transform = ElasticQueryValueTransform.None) =>
        new(ElasticQueryValueSourceKind.Constant, value, parameter: null, transform);

    /// <summary>Creates an invocation-parameter Elasticsearch query-template value.</summary>
    /// <param name="parameter">Canonical parameter identity.</param>
    /// <param name="transform">Deterministic target transformation.</param>
    /// <returns>A reusable parameter-value template.</returns>
    /// <exception cref="ArgumentException"><paramref name="parameter"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transform"/> is unsupported.</exception>
    public static ElasticQueryValueTemplate FromParameter(
        QueryParameterId parameter,
        ElasticQueryValueTransform transform = ElasticQueryValueTransform.None) =>
        new(ElasticQueryValueSourceKind.Parameter, default, parameter, transform);

    /// <summary>Resolves and transforms this value for one canonical invocation.</summary>
    /// <param name="parameters">Effective invocation values keyed by canonical parameter identity.</param>
    /// <returns>The physical scalar value materialized into the Elasticsearch SDK request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required parameter is absent, a value is unsupported, or the configured transformation cannot preserve it.
    /// </exception>
    public ObservationValue Bind(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var value = SourceKind switch
        {
            ElasticQueryValueSourceKind.Constant => Constant,
            ElasticQueryValueSourceKind.Parameter when Parameter is { } parameter
                && parameters.TryGetValue(parameter, out var supplied) => supplied,
            ElasticQueryValueSourceKind.Parameter => throw new ArgumentException(
                $"Canonical query parameter '{Parameter?.Value}' is required by the Elasticsearch template.",
                nameof(parameters)),
            _ => throw new ArgumentOutOfRangeException(nameof(SourceKind), SourceKind, "Unsupported Elasticsearch value source.")
        };
        if (!IsSupportedScalar(value))
            throw new ArgumentException("An Elasticsearch query-template value must be a supported scalar.", nameof(parameters));

        return Transform switch
        {
            ElasticQueryValueTransform.None => value,
            ElasticQueryValueTransform.WildcardSuffix => ObservationValue.FromString(
                "*" + EscapeWildcard(RequireText(value, Transform))),
            ElasticQueryValueTransform.ReverseUnicodeScalars => ObservationValue.FromString(
                ReverseUnicodeScalars(RequireText(value, Transform))),
            _ => throw new ArgumentOutOfRangeException(nameof(Transform), Transform, "Unsupported Elasticsearch value transform.")
        };
    }

    internal void AppendCanonical(StringBuilder builder)
    {
        ElasticCanonicalText.Append(builder, (int)SourceKind);
        ElasticCanonicalText.Append(builder, Parameter?.Value);
        ElasticCanonicalText.Append(builder, SourceKind == ElasticQueryValueSourceKind.Constant
            ? JsonSerializer.Serialize(Constant)
            : null);
        ElasticCanonicalText.Append(builder, (int)Transform);
    }

    internal static bool IsSupportedScalar(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Null
            or ObservationValueKind.Bool
            or ObservationValueKind.Int64 => true,
        ObservationValueKind.Double => double.IsFinite(value.Double),
        ObservationValueKind.String
            or ObservationValueKind.DateTimeOffset
            or ObservationValueKind.DateOnly
            or ObservationValueKind.TimeOnly
            or ObservationValueKind.TimeSpan => value.String is { } text && IsWellFormedUnicode(text),
        _ => false
    };

    internal static FieldValue ToFieldValue(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Null => FieldValue.Null,
        ObservationValueKind.Bool => FieldValue.Boolean(value.Bool),
        ObservationValueKind.Int64 => FieldValue.Long(value.Int64),
        ObservationValueKind.Double when double.IsFinite(value.Double) => FieldValue.Double(value.Double),
        ObservationValueKind.String
            or ObservationValueKind.DateTimeOffset
            or ObservationValueKind.DateOnly
            or ObservationValueKind.TimeOnly
            or ObservationValueKind.TimeSpan => FieldValue.String(value.String ?? string.Empty),
        _ => throw new ArgumentException(
            $"Value kind '{value.Kind}' cannot be materialized as an exact Elasticsearch SDK field value.",
            nameof(value))
    };

    static string RequireText(ObservationValue value, ElasticQueryValueTransform transform)
    {
        if (value.Kind != ObservationValueKind.String)
            throw new ArgumentException($"Elasticsearch transform '{transform}' requires a canonical text value.", nameof(value));
        return value.String ?? string.Empty;
    }

    static string EscapeWildcard(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("?", "\\?", StringComparison.Ordinal);

    static bool IsWellFormedUnicode(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
                return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    static string ReverseUnicodeScalars(string value)
    {
        List<Rune> runes = new(value.Length);
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
                throw new ArgumentException("A reversed Elasticsearch suffix value must contain well-formed Unicode scalars.", nameof(value));
            runes.Add(rune);
            remaining = remaining[consumed..];
        }

        StringBuilder result = new(value.Length);
        for (var index = runes.Count - 1; index >= 0; index--)
            result.Append(runes[index]);
        return result.ToString();
    }
}

/// <summary>Inclusivity of one physical Elasticsearch range boundary.</summary>
public enum ElasticRangeBoundKind
{
    /// <summary>The boundary value is excluded.</summary>
    Exclusive = 0,

    /// <summary>The boundary value is included.</summary>
    Inclusive = 1
}

/// <summary>One reusable lower or upper Elasticsearch range boundary.</summary>
public sealed record ElasticRangeBound
{
    /// <summary>Creates a range boundary.</summary>
    /// <param name="value">Reusable boundary value.</param>
    /// <param name="kind">Whether the boundary is inclusive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public ElasticRangeBound(ElasticQueryValueTemplate value, ElasticRangeBoundKind kind)
    {
        Value = Guard.RequireNotNull(value);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Elasticsearch range-bound kind.");
        Kind = kind;
    }

    /// <summary>Reusable boundary value.</summary>
    public ElasticQueryValueTemplate Value { get; }

    /// <summary>Whether the boundary includes its value.</summary>
    public ElasticRangeBoundKind Kind { get; }
}

/// <summary>Immutable Elasticsearch Query DSL clause template.</summary>
public sealed class ElasticQueryTemplate
{
    ElasticQueryTemplate(
        ElasticQueryTemplateKind kind,
        string? field = null,
        ElasticQueryValueTemplate? value = null,
        ElasticRangeBound? lower = null,
        ElasticRangeBound? upper = null,
        ImmutableArray<ElasticQueryTemplate> filter = default,
        ImmutableArray<ElasticQueryTemplate> should = default,
        ImmutableArray<ElasticQueryTemplate> mustNot = default)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Elasticsearch query-template kind.");
        Kind = kind;
        Field = field is null ? null : RequireField(field, nameof(field));
        Value = value;
        Lower = lower;
        Upper = upper;
        Filter = Normalize(filter, nameof(filter));
        Should = Normalize(should, nameof(should));
        MustNot = Normalize(mustNot, nameof(mustNot));

        switch (kind)
        {
            case ElasticQueryTemplateKind.MatchAll when Field is null && value is null:
                break;
            case ElasticQueryTemplateKind.Term or ElasticQueryTemplateKind.Wildcard or ElasticQueryTemplateKind.Prefix
                when Field is not null && value is not null:
                break;
            case ElasticQueryTemplateKind.Exists when Field is not null && value is null:
                break;
            case ElasticQueryTemplateKind.Range when Field is not null && value is null && (lower is not null || upper is not null):
                break;
            case ElasticQueryTemplateKind.Boolean when Field is null && value is null
                                                       && (!Filter.IsDefaultOrEmpty || !Should.IsDefaultOrEmpty || !MustNot.IsDefaultOrEmpty):
                break;
            default:
                throw new ArgumentException("Elasticsearch query-template members conflict with the selected clause kind.", nameof(kind));
        }
    }

    /// <summary>Physical query-clause kind.</summary>
    public ElasticQueryTemplateKind Kind { get; }

    /// <summary>Physical indexed field used by the clause, or <see langword="null"/>.</summary>
    public string? Field { get; }

    /// <summary>Reusable scalar value used by the clause, or <see langword="null"/>.</summary>
    public ElasticQueryValueTemplate? Value { get; }

    /// <summary>Optional lower range boundary.</summary>
    public ElasticRangeBound? Lower { get; }

    /// <summary>Optional upper range boundary.</summary>
    public ElasticRangeBound? Upper { get; }

    /// <summary>Non-scoring clauses that must all match.</summary>
    public ImmutableArray<ElasticQueryTemplate> Filter { get; }

    /// <summary>Clauses of which at least one must match.</summary>
    public ImmutableArray<ElasticQueryTemplate> Should { get; }

    /// <summary>Clauses that must not match.</summary>
    public ImmutableArray<ElasticQueryTemplate> MustNot { get; }

    /// <summary>Creates a match-all query clause.</summary>
    /// <returns>An immutable match-all clause.</returns>
    public static ElasticQueryTemplate MatchAll() => new(ElasticQueryTemplateKind.MatchAll);

    /// <summary>Creates an exact term query clause.</summary>
    /// <param name="field">Physical indexed field.</param>
    /// <param name="value">Reusable exact term.</param>
    /// <returns>An immutable term clause.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="field"/> is empty or contains a control character.</exception>
    public static ElasticQueryTemplate Term(string field, ElasticQueryValueTemplate value) =>
        new(ElasticQueryTemplateKind.Term, field, Guard.RequireNotNull(value));

    /// <summary>Creates a scalar range query clause.</summary>
    /// <param name="field">Physical indexed field.</param>
    /// <param name="lower">Optional lower boundary.</param>
    /// <param name="upper">Optional upper boundary.</param>
    /// <returns>An immutable range clause.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The field is invalid or both boundaries are absent.</exception>
    public static ElasticQueryTemplate Range(
        string field,
        ElasticRangeBound? lower = null,
        ElasticRangeBound? upper = null) =>
        new(ElasticQueryTemplateKind.Range, field, lower: lower, upper: upper);

    /// <summary>Creates an indexed-field existence query clause.</summary>
    /// <param name="field">Physical indexed field.</param>
    /// <returns>An immutable existence clause.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="field"/> is invalid.</exception>
    public static ElasticQueryTemplate Exists(string field) => new(ElasticQueryTemplateKind.Exists, field);

    /// <summary>Creates a wildcard query clause.</summary>
    /// <param name="field">Physical exact-value field.</param>
    /// <param name="pattern">Reusable escaped wildcard pattern.</param>
    /// <returns>An immutable wildcard clause.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="field"/> is invalid.</exception>
    public static ElasticQueryTemplate Wildcard(string field, ElasticQueryValueTemplate pattern) =>
        new(ElasticQueryTemplateKind.Wildcard, field, Guard.RequireNotNull(pattern));

    /// <summary>Creates a prefix query clause.</summary>
    /// <param name="field">Physical exact-value field.</param>
    /// <param name="prefix">Reusable exact prefix.</param>
    /// <returns>An immutable prefix clause.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="prefix"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="field"/> is invalid.</exception>
    public static ElasticQueryTemplate Prefix(string field, ElasticQueryValueTemplate prefix) =>
        new(ElasticQueryTemplateKind.Prefix, field, Guard.RequireNotNull(prefix));

    /// <summary>
    /// Creates a Boolean query clause. <paramref name="filter"/> executes in filter context; scoring for
    /// <paramref name="should"/> follows the clause's enclosing Elasticsearch query context.
    /// </summary>
    /// <param name="filter">Clauses that must all match.</param>
    /// <param name="should">Clauses of which at least one must match.</param>
    /// <param name="mustNot">Clauses that must not match.</param>
    /// <returns>An immutable Boolean clause.</returns>
    /// <exception cref="ArgumentException">Every collection is empty or a collection contains <see langword="null"/>.</exception>
    public static ElasticQueryTemplate Boolean(
        ImmutableArray<ElasticQueryTemplate> filter = default,
        ImmutableArray<ElasticQueryTemplate> should = default,
        ImmutableArray<ElasticQueryTemplate> mustNot = default) =>
        new(ElasticQueryTemplateKind.Boolean, filter: filter, should: should, mustNot: mustNot);

    internal ElasticQuery Bind(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters) => Kind switch
    {
        ElasticQueryTemplateKind.MatchAll => new MatchAllQuery(),
        ElasticQueryTemplateKind.Term => new TermQuery(
            new Field(Field!),
            ToRequiredFieldValue(Value!.Bind(parameters), "term")),
        ElasticQueryTemplateKind.Range => BindRange(parameters),
        ElasticQueryTemplateKind.Exists => new ExistsQuery(new Field(Field!)),
        ElasticQueryTemplateKind.Wildcard => new WildcardQuery(new Field(Field!))
        {
            Value = ToRequiredText(Value!.Bind(parameters), "wildcard")
        },
        ElasticQueryTemplateKind.Prefix => new PrefixQuery(
            new Field(Field!),
            ToRequiredText(Value!.Bind(parameters), "prefix")),
        ElasticQueryTemplateKind.Boolean => new BoolQuery
        {
            Filter = BindClauses(Filter, parameters),
            Should = BindClauses(Should, parameters),
            MinimumShouldMatch = Should.IsDefaultOrEmpty ? null : 1,
            MustNot = BindClauses(MustNot, parameters)
        },
        _ => throw new ArgumentOutOfRangeException(
            nameof(Kind),
            Kind,
            "Unsupported Elasticsearch query-template kind.")
    };

    internal void AppendCanonical(StringBuilder builder)
    {
        ElasticCanonicalText.Append(builder, (int)Kind);
        ElasticCanonicalText.Append(builder, Field);
        Value?.AppendCanonical(builder);
        AppendBound(builder, Lower);
        AppendBound(builder, Upper);
        AppendClauses(builder, Filter);
        AppendClauses(builder, Should);
        AppendClauses(builder, MustNot);
    }

    internal ImmutableHashSet<QueryParameterId> ReferencedParameters()
    {
        var parameters = ImmutableHashSet.CreateBuilder<QueryParameterId>();
        CollectParameters(parameters);
        return parameters.ToImmutable();
    }

    internal ImmutableHashSet<string> ReferencedFields()
    {
        var fields = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        CollectFields(fields);
        return fields.ToImmutable();
    }

    void CollectParameters(ImmutableHashSet<QueryParameterId>.Builder parameters)
    {
        AddParameter(Value, parameters);
        AddParameter(Lower?.Value, parameters);
        AddParameter(Upper?.Value, parameters);
        foreach (var clause in Filter)
            clause.CollectParameters(parameters);
        foreach (var clause in Should)
            clause.CollectParameters(parameters);
        foreach (var clause in MustNot)
            clause.CollectParameters(parameters);
    }

    void CollectFields(ImmutableHashSet<string>.Builder fields)
    {
        if (Field is not null)
            fields.Add(Field);
        foreach (var clause in Filter)
            clause.CollectFields(fields);
        foreach (var clause in Should)
            clause.CollectFields(fields);
        foreach (var clause in MustNot)
            clause.CollectFields(fields);
    }

    static void AddParameter(
        ElasticQueryValueTemplate? value,
        ImmutableHashSet<QueryParameterId>.Builder parameters)
    {
        if (value?.Parameter is { } parameter)
            parameters.Add(parameter);
    }

    ElasticQuery BindRange(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        var lowerValue = Lower?.Value.Bind(parameters);
        var upperValue = Upper?.Value.Bind(parameters);
        if (lowerValue is { } lower && lower.Kind == ObservationValueKind.Null
            || upperValue is { } upper && upper.Kind == ObservationValueKind.Null)
        {
            throw new ArgumentException(
                "An Elasticsearch range query requires non-null boundaries.",
                nameof(parameters));
        }

        var numeric = IsNumeric(lowerValue) || IsNumeric(upperValue);
        var textual = IsTextual(lowerValue) || IsTextual(upperValue);
        if (numeric && textual || !numeric && !textual)
        {
            throw new ArgumentException(
                "Elasticsearch range boundaries must share one numeric or textual scalar domain.",
                nameof(parameters));
        }
        return numeric
            ? BindNumberRange(lowerValue, upperValue)
            : BindTermRange(lowerValue, upperValue);
    }

    ElasticQuery BindNumberRange(ObservationValue? lowerValue, ObservationValue? upperValue)
    {
        NumberRangeQuery range = new(new Field(Field!));
        if (Lower is { } lower && lowerValue is { } lowerBound)
        {
            if (lower.Kind == ElasticRangeBoundKind.Inclusive)
                range.Gte = ToNumber(lowerBound);
            else
                range.Gt = ToNumber(lowerBound);
        }
        if (Upper is { } upper && upperValue is { } upperBound)
        {
            if (upper.Kind == ElasticRangeBoundKind.Inclusive)
                range.Lte = ToNumber(upperBound);
            else
                range.Lt = ToNumber(upperBound);
        }
        return range;
    }

    ElasticQuery BindTermRange(ObservationValue? lowerValue, ObservationValue? upperValue)
    {
        TermRangeQuery range = new(new Field(Field!));
        if (Lower is { } lower && lowerValue is { } lowerBound)
        {
            if (lower.Kind == ElasticRangeBoundKind.Inclusive)
                range.Gte = ToText(lowerBound);
            else
                range.Gt = ToText(lowerBound);
        }
        if (Upper is { } upper && upperValue is { } upperBound)
        {
            if (upper.Kind == ElasticRangeBoundKind.Inclusive)
                range.Lte = ToText(upperBound);
            else
                range.Lt = ToText(upperBound);
        }
        return range;
    }

    static FieldValue ToRequiredFieldValue(ObservationValue value, string queryName)
    {
        if (value.Kind == ObservationValueKind.Null)
        {
            throw new ArgumentException(
                $"An Elasticsearch '{queryName}' query requires a non-null indexed value. Use an existence clause to express null semantics.",
                nameof(value));
        }
        return ElasticQueryValueTemplate.ToFieldValue(value);
    }

    static string ToRequiredText(ObservationValue value, string queryName)
    {
        if (value.Kind != ObservationValueKind.String)
        {
            throw new ArgumentException(
                $"An Elasticsearch '{queryName}' query requires a canonical text value.",
                nameof(value));
        }
        return value.String ?? string.Empty;
    }

    static bool IsNumeric(ObservationValue? value) => value is
    {
        Kind: ObservationValueKind.Int64 or ObservationValueKind.Double
    };

    static bool IsTextual(ObservationValue? value) => value is
    {
        Kind: ObservationValueKind.String
            or ObservationValueKind.DateTimeOffset
            or ObservationValueKind.DateOnly
            or ObservationValueKind.TimeOnly
            or ObservationValueKind.TimeSpan
    };

    static Number ToNumber(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Int64 => new(value.Int64),
        ObservationValueKind.Double when double.IsFinite(value.Double) => new(value.Double),
        _ => throw new ArgumentException("An Elasticsearch numeric range requires a finite numeric value.", nameof(value))
    };

    static string ToText(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.String
            or ObservationValueKind.DateTimeOffset
            or ObservationValueKind.DateOnly
            or ObservationValueKind.TimeOnly
            or ObservationValueKind.TimeSpan => value.String ?? string.Empty,
        _ => throw new ArgumentException("An Elasticsearch textual range requires a canonical string representation.", nameof(value))
    };

    static ICollection<ElasticQuery>? BindClauses(
        ImmutableArray<ElasticQueryTemplate> clauses,
        IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters) =>
        clauses.IsDefaultOrEmpty
            ? null
            : [.. clauses.Select(clause => clause.Bind(parameters))];

    static ImmutableArray<ElasticQueryTemplate> Normalize(
        ImmutableArray<ElasticQueryTemplate> clauses,
        string parameterName)
    {
        var normalized = clauses.IsDefault ? [] : clauses;
        if (normalized.Any(static clause => clause is null))
            throw new ArgumentException("Elasticsearch Boolean clauses cannot contain null entries.", parameterName);
        return normalized;
    }

    static string RequireField(string field, string parameterName)
    {
        var normalized = Guard.RequireNotNullOrWhiteSpace(field);
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("An Elasticsearch field name cannot contain control characters.", parameterName);
        return normalized;
    }

    static void AppendBound(StringBuilder builder, ElasticRangeBound? bound)
    {
        ElasticCanonicalText.Append(builder, bound is null ? -1 : (int)bound.Kind);
        bound?.Value.AppendCanonical(builder);
    }

    static void AppendClauses(StringBuilder builder, ImmutableArray<ElasticQueryTemplate> clauses)
    {
        ElasticCanonicalText.Append(builder, clauses.Length);
        foreach (var clause in clauses)
            clause.AppendCanonical(builder);
    }
}

/// <summary>One exact physical Elasticsearch sort key.</summary>
public sealed record ElasticSearchSort
{
    /// <summary>Creates a sort key.</summary>
    /// <param name="field">Physical sortable field.</param>
    /// <param name="direction">Canonical sort direction.</param>
    /// <param name="nullPlacement">Canonical null placement.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="field"/> is empty or contains a control character.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> or <paramref name="nullPlacement"/> is unsupported.
    /// </exception>
    public ElasticSearchSort(
        string field,
        QuerySortDirection direction,
        QueryNullPlacement nullPlacement)
    {
        Field = Guard.RequireNotNullOrWhiteSpace(field);
        if (Field.Any(char.IsControl))
            throw new ArgumentException("An Elasticsearch sort field cannot contain control characters.", nameof(field));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported query sort direction.");
        if (!Enum.IsDefined(nullPlacement))
            throw new ArgumentOutOfRangeException(nameof(nullPlacement), nullPlacement, "Unsupported query null placement.");
        Direction = direction;
        NullPlacement = nullPlacement;
    }

    /// <summary>Physical sortable field.</summary>
    public string Field { get; }

    /// <summary>Canonical sort direction.</summary>
    public QuerySortDirection Direction { get; }

    /// <summary>Canonical null placement.</summary>
    public QueryNullPlacement NullPlacement { get; }

    internal SortOptions Bind() => new()
    {
        Field = new FieldSort(new Field(Field))
        {
            Order = Direction == QuerySortDirection.Ascending
                ? ElasticSortOrder.Asc
                : ElasticSortOrder.Desc,
            Missing = NullPlacement == QueryNullPlacement.First ? "_first" : "_last"
        }
    };

    internal void AppendCanonical(StringBuilder builder)
    {
        ElasticCanonicalText.Append(builder, Field);
        ElasticCanonicalText.Append(builder, (int)Direction);
        ElasticCanonicalText.Append(builder, (int)NullPlacement);
    }
}

/// <summary>Physical pagination form emitted in one Elasticsearch request template.</summary>
public enum ElasticSearchPageKind
{
    /// <summary>The request is not paged.</summary>
    None = 0,

    /// <summary>The request uses <c>from</c> and <c>size</c>.</summary>
    Offset = 1,

    /// <summary>
    /// The request uses stable keyset pagination, emitting <c>search_after</c> after the first page.
    /// </summary>
    SearchAfter = 2
}

/// <summary>Immutable Elasticsearch page template.</summary>
public sealed class ElasticSearchPageTemplate
{
    ElasticSearchPageTemplate(
        ElasticSearchPageKind kind,
        int offset,
        int limit,
        ImmutableArray<ElasticQueryValueTemplate> after)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Elasticsearch page kind.");
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "An Elasticsearch page offset cannot be negative.");
        if (kind != ElasticSearchPageKind.None && limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A paged Elasticsearch request requires a positive limit.");
        var normalizedAfter = after.IsDefault ? [] : after;
        if (normalizedAfter.Any(static value => value is null))
            throw new ArgumentException("Elasticsearch search-after values cannot contain null templates.", nameof(after));
        if (kind != ElasticSearchPageKind.SearchAfter && !normalizedAfter.IsDefaultOrEmpty)
            throw new ArgumentException("Only search-after paging can retain continuation values.", nameof(after));
        if (kind != ElasticSearchPageKind.Offset && offset != 0)
            throw new ArgumentException("Only offset paging can retain a non-zero offset.", nameof(offset));

        Kind = kind;
        Offset = offset;
        Limit = limit;
        After = normalizedAfter;
    }

    /// <summary>Unpaged request template.</summary>
    public static ElasticSearchPageTemplate Unpaged { get; } = new(
        ElasticSearchPageKind.None,
        offset: 0,
        limit: 0,
        after: []);

    /// <summary>Physical page kind.</summary>
    public ElasticSearchPageKind Kind { get; }

    /// <summary>Number of ordered hits skipped by offset paging.</summary>
    public int Offset { get; }

    /// <summary>Maximum hits or buckets returned by a paged request.</summary>
    public int Limit { get; }

    /// <summary>Ordered search-after values, or an empty array for the first keyset page.</summary>
    public ImmutableArray<ElasticQueryValueTemplate> After { get; }

    /// <summary>Creates offset pagination.</summary>
    /// <param name="offset">Number of ordered hits to skip.</param>
    /// <param name="limit">Maximum hits returned.</param>
    /// <returns>An immutable offset-page template.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="limit"/> is not positive.</exception>
    public static ElasticSearchPageTemplate OffsetPage(int offset, int limit) =>
        new(ElasticSearchPageKind.Offset, offset, limit, after: []);

    /// <summary>Creates search-after pagination.</summary>
    /// <param name="limit">Maximum hits returned.</param>
    /// <param name="after">
    /// Ordered continuation values aligned with physical sorts, or an empty array for the first keyset page.
    /// </param>
    /// <returns>An immutable search-after template.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="after"/> contains <see langword="null"/>.</exception>
    public static ElasticSearchPageTemplate SearchAfterPage(
        int limit,
        ImmutableArray<ElasticQueryValueTemplate> after) =>
        new(ElasticSearchPageKind.SearchAfter, offset: 0, limit, after);

    internal void AppendCanonical(StringBuilder builder)
    {
        ElasticCanonicalText.Append(builder, (int)Kind);
        ElasticCanonicalText.Append(builder, Offset);
        ElasticCanonicalText.Append(builder, Limit);
        ElasticCanonicalText.Append(builder, After.Length);
        foreach (var value in After)
            value.AppendCanonical(builder);
    }
}

/// <summary>Physical Elasticsearch aggregation request represented by a canonical branch template.</summary>
public enum ElasticAggregationTemplateKind
{
    /// <summary>No aggregation request is emitted.</summary>
    None = 0,

    /// <summary>Exact filtered row count is read from exact total hits.</summary>
    GlobalCount = 1,

    /// <summary>Exact paged grouped counts are read from a composite aggregation.</summary>
    CompositeCount = 2
}

/// <summary>One exact grouping source in an Elasticsearch composite aggregation.</summary>
public sealed record ElasticCompositeAggregationSource
{
    /// <summary>Creates a composite grouping source.</summary>
    /// <param name="name">Stable request-local source name.</param>
    /// <param name="field">Physical aggregatable field.</param>
    /// <param name="direction">Canonical key ordering direction.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A string is empty or contains a control character.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is unsupported.</exception>
    public ElasticCompositeAggregationSource(
        string name,
        string field,
        QuerySortDirection direction)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Field = Guard.RequireNotNullOrWhiteSpace(field);
        if (Name.Any(char.IsControl) || Field.Any(char.IsControl))
            throw new ArgumentException("Composite aggregation names and fields cannot contain control characters.");
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported grouping direction.");
        Direction = direction;
    }

    /// <summary>Stable request-local source name.</summary>
    public string Name { get; }

    /// <summary>Physical aggregatable field.</summary>
    public string Field { get; }

    /// <summary>Canonical group-key order.</summary>
    public QuerySortDirection Direction { get; }
}

/// <summary>Immutable exact Elasticsearch aggregation template.</summary>
public sealed class ElasticAggregationTemplate
{
    ElasticAggregationTemplate(
        ElasticAggregationTemplateKind kind,
        string? name = null,
        int size = 0,
        ImmutableArray<ElasticCompositeAggregationSource> sources = default,
        ImmutableArray<ElasticQueryValueTemplate> after = default)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Elasticsearch aggregation-template kind.");
        Kind = kind;
        Name = name;
        Size = size;
        Sources = sources.IsDefault ? [] : sources;
        After = after.IsDefault ? [] : after;
        if (kind == ElasticAggregationTemplateKind.CompositeCount && size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "A composite aggregation requires a positive page size.");
        if (Sources.Any(static source => source is null) || After.Any(static value => value is null))
            throw new ArgumentException("Elasticsearch aggregation templates cannot contain null entries.");
        if (Sources.GroupBy(static source => source.Name, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Composite aggregation source names cannot be repeated.", nameof(sources));

        switch (kind)
        {
            case ElasticAggregationTemplateKind.None when name is null && size == 0 && Sources.IsDefaultOrEmpty && After.IsDefaultOrEmpty:
            case ElasticAggregationTemplateKind.GlobalCount when name is null && size == 0 && Sources.IsDefaultOrEmpty && After.IsDefaultOrEmpty:
                break;
            case ElasticAggregationTemplateKind.CompositeCount
                when !string.IsNullOrWhiteSpace(name) && size > 0 && !Sources.IsDefaultOrEmpty
                     && (After.IsDefaultOrEmpty || After.Length == Sources.Length):
                break;
            default:
                throw new ArgumentException("Aggregation-template members conflict with the selected kind.", nameof(kind));
        }
    }

    /// <summary>No aggregation request.</summary>
    public static ElasticAggregationTemplate None { get; } = new(ElasticAggregationTemplateKind.None);

    /// <summary>Physical aggregation kind.</summary>
    public ElasticAggregationTemplateKind Kind { get; }

    /// <summary>Request-local aggregation name, or <see langword="null"/>.</summary>
    public string? Name { get; }

    /// <summary>Maximum composite buckets returned.</summary>
    public int Size { get; }

    /// <summary>Ordered composite grouping sources.</summary>
    public ImmutableArray<ElasticCompositeAggregationSource> Sources { get; }

    /// <summary>Optional ordered composite continuation values.</summary>
    public ImmutableArray<ElasticQueryValueTemplate> After { get; }

    /// <summary>Creates an exact filtered row-count template.</summary>
    /// <returns>A global-count template using exact total hits.</returns>
    public static ElasticAggregationTemplate CountRows() => new(ElasticAggregationTemplateKind.GlobalCount);

    /// <summary>Creates a paged exact composite grouped-count template.</summary>
    /// <param name="name">Stable aggregation name.</param>
    /// <param name="size">Maximum buckets returned.</param>
    /// <param name="sources">Ordered exact grouping sources.</param>
    /// <param name="after">Optional continuation values aligned with <paramref name="sources"/>.</param>
    /// <returns>An immutable composite-count template.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required value is absent, repeated, or inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not positive.</exception>
    public static ElasticAggregationTemplate CompositeCount(
        string name,
        int size,
        ImmutableArray<ElasticCompositeAggregationSource> sources,
        ImmutableArray<ElasticQueryValueTemplate> after = default) =>
        new(ElasticAggregationTemplateKind.CompositeCount, Guard.RequireNotNullOrWhiteSpace(name), size, sources, after);

    internal Aggregation Bind(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        if (Kind != ElasticAggregationTemplateKind.CompositeCount)
        {
            throw new InvalidOperationException(
                "Only a composite-count template materializes an Elasticsearch SDK aggregation node.");
        }

        CompositeAggregation composite = new()
        {
            Size = Size,
            Sources =
            [
                .. Sources.Select(static source =>
                    new KeyValuePair<string, CompositeAggregationSource>(
                        source.Name,
                        new()
                        {
                            Terms = new()
                            {
                                Field = new Field(source.Field),
                                Order = source.Direction == QuerySortDirection.Ascending
                                    ? ElasticSortOrder.Asc
                                    : ElasticSortOrder.Desc,
                                MissingBucket = false
                            }
                        }))
            ]
        };
        if (!After.IsDefaultOrEmpty)
        {
            Dictionary<Field, FieldValue> after = new(Sources.Length);
            for (var index = 0; index < Sources.Length; index++)
            {
                var value = After[index].Bind(parameters);
                if (value.Kind == ObservationValueKind.Null)
                {
                    throw new ArgumentException(
                        "A composite continuation cannot contain null when missing buckets are disabled.",
                        nameof(parameters));
                }
                after.Add(new(Sources[index].Name), ElasticQueryValueTemplate.ToFieldValue(value));
            }
            composite.After = after;
        }
        return new() { Composite = composite };
    }

    internal void AppendCanonical(StringBuilder builder)
    {
        ElasticCanonicalText.Append(builder, (int)Kind);
        ElasticCanonicalText.Append(builder, Name);
        ElasticCanonicalText.Append(builder, Size);
        ElasticCanonicalText.Append(builder, Sources.Length);
        foreach (var source in Sources)
        {
            ElasticCanonicalText.Append(builder, source.Name);
            ElasticCanonicalText.Append(builder, source.Field);
            ElasticCanonicalText.Append(builder, (int)source.Direction);
        }
        ElasticCanonicalText.Append(builder, After.Length);
        foreach (var value in After)
            value.AppendCanonical(builder);
    }
}

/// <summary>Reusable immutable template that materializes Elasticsearch SDK search requests.</summary>
public sealed class ElasticSearchRequestTemplate
{
    /// <summary>Creates an Elasticsearch request template.</summary>
    /// <param name="index">Physical index or alias identity.</param>
    /// <param name="query">
    /// Physical query clause. Scoring follows its Elasticsearch query context; the canonical relation/query compiler
    /// places semantic predicates under filter context.
    /// </param>
    /// <param name="sourceIncludes">Exact physical <c>_source</c> selectors required by row decoding.</param>
    /// <param name="sorts">Stable physical sort keys.</param>
    /// <param name="page">Physical pagination contract.</param>
    /// <param name="aggregation">Optional exact aggregation contract.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="index"/>, <paramref name="query"/>, <paramref name="page"/>, or
    /// <paramref name="aggregation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The index is empty, a collection contains an invalid entry, search-after pagination is not backed by stable
    /// sorting, continuation arity differs from sorting, or an aggregation conflicts with hit pagination or sorting.
    /// </exception>
    public ElasticSearchRequestTemplate(
        string index,
        ElasticQueryTemplate query,
        ImmutableArray<string> sourceIncludes,
        ImmutableArray<ElasticSearchSort> sorts,
        ElasticSearchPageTemplate page,
        ElasticAggregationTemplate aggregation)
    {
        Index = Guard.RequireNotNullOrWhiteSpace(index);
        if (Index.Any(char.IsControl))
            throw new ArgumentException("An Elasticsearch index identity cannot contain control characters.", nameof(index));
        Query = Guard.RequireNotNull(query);
        var normalizedSources = sourceIncludes.IsDefault ? [] : sourceIncludes;
        if (normalizedSources.Any(string.IsNullOrWhiteSpace) || normalizedSources.Any(static source => source.Any(char.IsControl)))
            throw new ArgumentException("Elasticsearch source selectors must be non-empty and contain no control characters.", nameof(sourceIncludes));
        SourceIncludes = [.. normalizedSources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var normalizedSorts = sorts.IsDefault ? [] : sorts;
        if (normalizedSorts.Any(static sort => sort is null))
            throw new ArgumentException("Elasticsearch sorts cannot contain null entries.", nameof(sorts));
        Sorts = normalizedSorts;
        Page = Guard.RequireNotNull(page);
        Aggregation = Guard.RequireNotNull(aggregation);
        if (Page.Kind == ElasticSearchPageKind.SearchAfter && Sorts.IsDefaultOrEmpty)
            throw new ArgumentException("Search-after pagination requires at least one physical sort key.", nameof(sorts));
        if (Page.Kind == ElasticSearchPageKind.SearchAfter
            && !Page.After.IsDefaultOrEmpty
            && Page.After.Length != Sorts.Length)
        {
            throw new ArgumentException("Search-after continuation values must align with every physical sort key.", nameof(page));
        }
        if (Aggregation.Kind != ElasticAggregationTemplateKind.None && Page.Kind != ElasticSearchPageKind.None)
            throw new ArgumentException("Hit pagination cannot be combined with an aggregation template.", nameof(page));
        if (Aggregation.Kind != ElasticAggregationTemplateKind.None && !Sorts.IsDefaultOrEmpty)
            throw new ArgumentException("Hit sorting cannot be combined with an aggregation template.", nameof(sorts));
        if (Aggregation.Kind != ElasticAggregationTemplateKind.None && !SourceIncludes.IsDefaultOrEmpty)
            throw new ArgumentException("Source selection cannot be combined with an aggregation template.", nameof(sourceIncludes));
    }

    /// <summary>Physical index or alias identity.</summary>
    public string Index { get; }

    /// <summary>Physical query clause whose scoring behavior follows its Elasticsearch query context.</summary>
    public ElasticQueryTemplate Query { get; }

    /// <summary>Exact physical <c>_source</c> selectors required by row decoding.</summary>
    public ImmutableArray<string> SourceIncludes { get; }

    /// <summary>Stable physical sort keys.</summary>
    public ImmutableArray<ElasticSearchSort> Sorts { get; }

    /// <summary>Physical hit pagination contract.</summary>
    public ElasticSearchPageTemplate Page { get; }

    /// <summary>Optional exact aggregation contract.</summary>
    public ElasticAggregationTemplate Aggregation { get; }

    /// <summary>Binds invocation values and materializes a fresh Elasticsearch SDK request.</summary>
    /// <param name="parameters">Effective canonical invocation values.</param>
    /// <returns>
    /// A new mutable SDK request owned by the caller. Its initial state is covered by this template; caller mutation
    /// is an explicit escape hatch outside the template's exactness and fingerprint guarantees.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required value is missing, unsupported, or cannot be transformed.</exception>
    public SearchRequest Bind(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Indices indices = Index;
        SearchRequest request = new(indices)
        {
            AllowPartialSearchResults = false,
            Query = Query.Bind(parameters),
            Source = Aggregation.Kind == ElasticAggregationTemplateKind.None
                ? BindSource()
                : new SourceConfig(false),
            Sort = Sorts.IsDefaultOrEmpty ? null : [.. Sorts.Select(static sort => sort.Bind())]
        };

        switch (Page.Kind)
        {
            case ElasticSearchPageKind.None:
                break;
            case ElasticSearchPageKind.Offset:
                request.From = Page.Offset;
                request.Size = Page.Limit;
                break;
            case ElasticSearchPageKind.SearchAfter:
                request.Size = Page.Limit;
                if (!Page.After.IsDefaultOrEmpty)
                {
                    request.SearchAfter =
                    [
                        .. Page.After.Select(value =>
                            ElasticQueryValueTemplate.ToFieldValue(value.Bind(parameters)))
                    ];
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(Page),
                    Page.Kind,
                    "Unsupported Elasticsearch page kind.");
        }

        if (Aggregation.Kind == ElasticAggregationTemplateKind.GlobalCount)
        {
            request.Size = 0;
            request.TrackTotalHits = new(true);
        }
        else if (Aggregation.Kind == ElasticAggregationTemplateKind.CompositeCount)
        {
            request.Size = 0;
            request.Aggregations = new Dictionary<string, Aggregation>(StringComparer.Ordinal)
            {
                [Aggregation.Name!] = Aggregation.Bind(parameters)
            };
        }

        return request;
    }

    SourceConfig BindSource() => SourceIncludes.IsDefaultOrEmpty
        ? new(false)
        : new(new SourceFilter { Includes = Fields.FromStrings([.. SourceIncludes]) });

    internal string CanonicalText()
    {
        StringBuilder builder = new();
        ElasticCanonicalText.Append(builder, Index);
        Query.AppendCanonical(builder);
        ElasticCanonicalText.Append(builder, SourceIncludes.Length);
        foreach (var source in SourceIncludes)
            ElasticCanonicalText.Append(builder, source);
        ElasticCanonicalText.Append(builder, Sorts.Length);
        foreach (var sort in Sorts)
            sort.AppendCanonical(builder);
        Page.AppendCanonical(builder);
        Aggregation.AppendCanonical(builder);
        return builder.ToString();
    }
}

static class ElasticCanonicalText
{
    internal static void Append(StringBuilder builder, string? value)
    {
        builder
            .Append(value?.Length ?? -1)
            .Append(':')
            .Append(value)
            .Append(';');
    }

    internal static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));
}
