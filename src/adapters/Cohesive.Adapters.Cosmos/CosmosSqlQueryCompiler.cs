using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Relations.Queries;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Parameterized Cosmos SQL query.
/// </summary>
public sealed record CosmosSqlQuery(
    string Text,
    IReadOnlyDictionary<string, object?> Parameters
    )
{
    /// <summary>
    /// Converts the structured SQL query to the Cosmos SDK query definition.
    /// </summary>
    public QueryDefinition ToQueryDefinition()
    {
        var query = new QueryDefinition(Text);
        foreach (var (name, value) in Parameters)
            query = query.WithParameter(name, value);
        return query;
    }
}

/// <summary>
/// Compiles relation queries to parameterized Cosmos SQL.
/// </summary>
public sealed class CosmosSqlQueryCompiler : IQueryCompiler<CosmosSqlQuery>
{
    /// <inheritdoc />
    public QueryCapabilitySet Capabilities { get; } = new(
        QueryCapability.Equality
        | QueryCapability.Prefix
        | QueryCapability.Suffix
        | QueryCapability.Contains
        | QueryCapability.Exists
        | QueryCapability.NumberRange
        | QueryCapability.DateRange
        | QueryCapability.SetMembership
        | QueryCapability.NestedAny
        | QueryCapability.ScopedFields
        | QueryCapability.Negation
        | QueryCapability.CaseInsensitiveStringComparison);

    /// <inheritdoc />
    public CosmosSqlQuery Compile(EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        Capabilities.EnsureSupports(
            QueryCapabilityInspector.GetRequiredCapabilities(predicate).Value,
            operation: $"compile predicate with '{nameof(CosmosSqlQueryCompiler)}'"
            );

        var state = new CosmosSqlPredicateCompiler();
        return state.CompileQuery(predicate);
    }

    internal sealed class CosmosSqlPredicateCompiler
    {
        readonly Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        int nextParameterIndex;
        int nextAliasIndex;

        public IReadOnlyDictionary<string, object?> Parameters => parameters;

        public CosmosSqlQuery CompileQuery(EntityPredicate predicate)
        {
            var where = CompilePredicate(predicate, rootAlias: "c");
            return new(Text: $"SELECT * FROM c WHERE {where}", Parameters: parameters);
        }

        public string CompilePredicate(EntityPredicate predicate, string rootAlias)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentException.ThrowIfNullOrWhiteSpace(rootAlias);

            var normalized = predicate.Predicate.Normalize();
            return predicate.Scope is { } scope
                ? CompileScopedPredicate(scope, normalized, rootAlias)
                : CompileFieldPredicate(normalized, alias: rootAlias);
        }

        public string CompileScalarField(FieldPath field, string alias = "c") =>
            CompileFieldAccess(alias, field);

        string CompileScopedPredicate(FieldPath scope, BoolExpr<FieldPredicate> predicate, string rootAlias)
        {
            var scopeAlias = NextAlias("scope");
            var scopeSource = CompileCollectionAccess(rootAlias, scope);
            return $"EXISTS (SELECT VALUE {scopeAlias} FROM {scopeAlias} IN {scopeSource} WHERE {CompileFieldPredicate(predicate, scopeAlias)})";
        }

        string CompileFieldPredicate(BoolExpr<FieldPredicate> expr, string alias) => expr switch
        {
            Atom<FieldPredicate> atom => CompileValuePredicate(fieldExpression: CompileFieldAccess(alias, atom.Term.Field), expr: atom.Term.Predicate.Normalize()),
            And<FieldPredicate> conjunction => Join("AND", conjunction.Terms.Select(term => CompileFieldPredicate(term, alias))),
            Or<FieldPredicate> disjunction => Join("OR", disjunction.Terms.Select(term => CompileFieldPredicate(term, alias))),
            Not<FieldPredicate> negation => $"NOT ({CompileFieldPredicate(negation.Term, alias)})",
            _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
        };

        string CompileValuePredicate(string fieldExpression, BoolExpr<ValuePredicate> expr) => expr switch
        {
            Atom<ValuePredicate> atom => CompileValueAtom(fieldExpression, atom.Term),
            And<ValuePredicate> conjunction => Join("AND", conjunction.Terms.Select(term => CompileValuePredicate(fieldExpression, term))),
            Or<ValuePredicate> disjunction => Join("OR", disjunction.Terms.Select(term => CompileValuePredicate(fieldExpression, term))),
            Not<ValuePredicate> negation => $"NOT ({CompileValuePredicate(fieldExpression, negation.Term)})",
            _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
        };

        string CompileValueAtom(string fieldExpression, ValuePredicate predicate) => predicate switch
        {
            ExactValuePredicate exact => CompileExact(fieldExpression, exact),
            BoolValuePredicate flag => $"{fieldExpression} = {AddParameter(flag.Value)}",
            IntValuePredicate value => $"{fieldExpression} = {AddParameter(value.Value)}",
            LongValuePredicate value => $"{fieldExpression} = {AddParameter(value.Value)}",
            DoubleValuePredicate value => $"{fieldExpression} = {AddParameter(value.Value)}",
            DecimalValuePredicate value => $"{fieldExpression} = {AddParameter(value.Value)}",
            DateValuePredicate value => $"{fieldExpression} = {AddParameter(ToIsoString(value.Value))}",
            PrefixValuePredicate prefix => CompileStringComparison("STARTSWITH", fieldExpression, prefix.Prefix, prefix.CaseSensitive),
            SuffixValuePredicate suffix => CompileStringComparison("ENDSWITH", fieldExpression, suffix.Suffix, suffix.CaseSensitive),
            ContainsValuePredicate contains => CompileStringComparison("CONTAINS", fieldExpression, contains.Value, contains.CaseSensitive),
            ExistsValuePredicate => $"IS_DEFINED({fieldExpression})",
            DateRangeValuePredicate range => CompileDateRange(fieldExpression, range),
            NumberRangeValuePredicate range => CompileNumberRange(fieldExpression, range),
            InValuePredicate set => CompileSetMembership(fieldExpression, set.Values),
            AnyValuePredicate any => CompileAnyValue(fieldExpression, any.Predicate.Normalize()),
            AnyFieldPredicate any => CompileAnyField(fieldExpression, any.Predicate.Normalize()),
            FullTextValuePredicate => throw new NotSupportedException("Cosmos SQL compilation does not support full-text predicates."),
            GeoDistanceValuePredicate => throw new NotSupportedException("Cosmos SQL compilation does not support geo-distance predicates."),
            _ => throw new InvalidOperationException($"Unsupported value-predicate type '{predicate.GetType().Name}'.")
        };

        string CompileExact(string fieldExpression, ExactValuePredicate exact) =>
            exact.CaseSensitive
                ? $"{fieldExpression} = {AddParameter(exact.Value)}"
                : $"STRINGEQUALS({fieldExpression}, {AddParameter(exact.Value)}, true)";

        string CompileStringComparison(string functionName, string fieldExpression, string value, bool caseSensitive)
        {
            var expression = $"{functionName}({fieldExpression}, {AddParameter(value)}";
            return caseSensitive ? $"{expression})" : $"{expression}, true)";
        }

        string CompileDateRange(string fieldExpression, DateRangeValuePredicate range)
        {
            List<string> clauses = [];
            if (range.Start is { } start)
            {
                var op = range.StartExclusive == true ? ">" : ">=";
                clauses.Add($"{fieldExpression} {op} {AddParameter(ToIsoString(start))}");
            }

            if (range.End is { } end)
            {
                var op = range.EndExclusive == true ? "<" : "<=";
                clauses.Add($"{fieldExpression} {op} {AddParameter(ToIsoString(end))}");
            }

            return Join("AND", clauses, emptyFallback: "(1 = 1)");
        }

        string CompileNumberRange(string fieldExpression, NumberRangeValuePredicate range)
        {
            List<string> clauses = [];
            if (range.Start is { } start)
            {
                var op = range.StartExclusive == true ? ">" : ">=";
                clauses.Add($"{fieldExpression} {op} {AddParameter(start)}");
            }

            if (range.End is { } end)
            {
                var op = range.EndExclusive == true ? "<" : "<=";
                clauses.Add($"{fieldExpression} {op} {AddParameter(end)}");
            }

            return Join("AND", clauses, emptyFallback: "(1 = 1)");
        }

        string CompileSetMembership(string fieldExpression, IReadOnlyCollection<object> values)
        {
            if (values.Count == 0)
                return "(1 = 0)";

            return Join(
                "OR",
                values.Select(value => $"{fieldExpression} = {AddParameter(ConvertParameterValue(value))}")
                );
        }

        string CompileAnyValue(string fieldExpression, BoolExpr<ValuePredicate> predicate)
        {
            var alias = NextAlias("any");
            return $"EXISTS (SELECT VALUE {alias} FROM {alias} IN {fieldExpression} WHERE {CompileValuePredicate(alias, predicate)})";
        }

        string CompileAnyField(string fieldExpression, BoolExpr<FieldPredicate> predicate)
        {
            var alias = NextAlias("any");
            return $"EXISTS (SELECT VALUE {alias} FROM {alias} IN {fieldExpression} WHERE {CompileFieldPredicate(predicate, alias)})";
        }

        public string AddParameter(object? value)
        {
            var name = $"@p{nextParameterIndex.ToString(CultureInfo.InvariantCulture)}";
            nextParameterIndex++;
            parameters[name] = value;
            return name;
        }

        string NextAlias(string prefix)
        {
            var alias = $"{prefix}{nextAliasIndex.ToString(CultureInfo.InvariantCulture)}";
            nextAliasIndex++;
            return alias;
        }

        public static string CompileFieldAccess(string alias, FieldPath field)
        {
            StringBuilder builder = new(alias);
            foreach (var segment in field.Segments)
            {
                switch (segment.Kind)
                {
                    case SegmentKind.Field:
                        builder.Append('[').Append(FormatQueryStringLiteral(segment.Segment!)).Append(']');
                        break;
                    case SegmentKind.Element:
                        throw new NotSupportedException($"Cosmos SQL field access does not support element segment '{field}'. Use '{nameof(AnyValuePredicate)}', '{nameof(AnyFieldPredicate)}', or predicate scope instead.");
                    default:
                        throw new InvalidOperationException($"Unsupported field-path segment kind '{segment.Kind}'.");
                }
            }

            return builder.ToString();
        }

        static string CompileCollectionAccess(string alias, FieldPath field)
        {
            StringBuilder builder = new(alias);
            var sawElement = false;
            foreach (var segment in field.Segments)
            {
                switch (segment.Kind)
                {
                    case SegmentKind.Field:
                        if (sawElement)
                        {
                            throw new NotSupportedException(
                                $"Cosmos SQL collection scope '{field}' cannot navigate beyond an element segment without another explicit '{nameof(AnyFieldPredicate)}' or scoped predicate.");
                        }

                        builder.Append('[').Append(FormatQueryStringLiteral(segment.Segment!)).Append(']');
                        break;
                    case SegmentKind.Element:
                        if (sawElement)
                            throw new NotSupportedException($"Cosmos SQL collection scope '{field}' contains multiple element segments.");

                        sawElement = true;
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported field-path segment kind '{segment.Kind}'.");
                }
            }

            return builder.ToString();
        }

        static string Join(string op, IEnumerable<string> terms, string? emptyFallback = null)
        {
            var materialized = terms.Where(static term => !string.IsNullOrWhiteSpace(term)).ToArray();
            return materialized.Length switch
            {
                0 when emptyFallback is not null => emptyFallback,
                0 => throw new InvalidOperationException($"Cannot compile empty '{op}' expression."),
                1 => materialized[0],
                _ => $"({string.Join($" {op} ", materialized)})"
            };
        }

        static object ConvertParameterValue(object value) => Guard.RequireNotNull(value) switch
        {
            string text => text,
            bool flag => flag,
            byte number => (long)number,
            short number => (long)number,
            int number => (long)number,
            long number => number,
            float number => (double)number,
            double number => number,
            decimal number => number,
            DateTime dateTime => ToIsoString(new DateTimeOffset(dateTime)),
            DateTimeOffset dateTimeOffset => ToIsoString(dateTimeOffset),
            Guid guid => guid.ToString(),
            _ => throw new NotSupportedException($"Cosmos SQL parameterization does not support CLR type '{value.GetType().FullName}'.")
        };

        static string ToIsoString(DateTimeOffset value) =>
            value.ToString("O", CultureInfo.InvariantCulture);

        static string FormatQueryStringLiteral(string value) =>
            JsonSerializer.Serialize(Guard.RequireNotNull(value));
    }
}
