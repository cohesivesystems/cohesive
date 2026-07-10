namespace Cohesive.Relations.Queries;

/// <summary>
/// Backend-query features that may or may not be supported by a compiler or storage adapter.
/// </summary>
[Flags]
public enum QueryCapability
{
    /// <summary>Represents the absence of a selected option.</summary>
    None = 0,
    /// <summary>Represents the equality option.</summary>
    Equality = 1 << 0,
    /// <summary>Represents the prefix option.</summary>
    Prefix = 1 << 1,
    /// <summary>Represents the suffix option.</summary>
    Suffix = 1 << 2,
    /// <summary>Represents the contains option.</summary>
    Contains = 1 << 3,
    /// <summary>Represents the full text option.</summary>
    FullText = 1 << 4,
    /// <summary>Represents the exists option.</summary>
    Exists = 1 << 5,
    /// <summary>Represents the number range option.</summary>
    NumberRange = 1 << 6,
    /// <summary>Represents the date range option.</summary>
    DateRange = 1 << 7,
    /// <summary>Represents the set membership option.</summary>
    SetMembership = 1 << 8,
    /// <summary>Represents the nested any option.</summary>
    NestedAny = 1 << 9,
    /// <summary>Represents the geo distance option.</summary>
    GeoDistance = 1 << 10,
    /// <summary>Represents the scoped fields option.</summary>
    ScopedFields = 1 << 11,
    /// <summary>Represents the negation option.</summary>
    Negation = 1 << 12,
    /// <summary>Represents the aggregation option.</summary>
    Aggregation = 1 << 13,
    /// <summary>Represents the case insensitive string comparison option.</summary>
    CaseInsensitiveStringComparison = 1 << 14
}

/// <summary>
/// Immutable set of supported query capabilities.
/// </summary>
public readonly record struct QueryCapabilitySet(QueryCapability Value)
{
    /// <summary>
    /// Empty capability set.
    /// </summary>
    public static QueryCapabilitySet None { get; } = new(QueryCapability.None);

    /// <summary>
    /// Returns <see langword="true" /> when every requested capability is present.
    /// </summary>
    public bool Supports(QueryCapability capability) =>
        (Value & capability) == capability;

    /// <summary>
    /// Returns a new capability set with the requested capability added.
    /// </summary>
    public QueryCapabilitySet With(QueryCapability capability) =>
        new(Value | capability);

    /// <summary>
    /// Throws when the required capabilities are not present.
    /// </summary>
    public void EnsureSupports(QueryCapability required, string? operation = null)
    {
        if (Supports(required))
            return;

        var missing = required & ~Value;
        var label = string.IsNullOrWhiteSpace(operation) ? "query operation" : operation;
        throw new NotSupportedException($"{label} requires unsupported query capabilities: {missing}.");
    }

    /// <summary>
    /// Returns a readable comma-separated capability list.
    /// </summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Describes a compiler that lowers a structured predicate into a backend-specific representation.
/// </summary>
public interface IQueryCompiler<out TCompiledQuery>
{
    /// <summary>
    /// Capabilities supported by the compiler.
    /// </summary>
    QueryCapabilitySet Capabilities { get; }

    /// <summary>
    /// Compiles the supplied structured predicate.
    /// </summary>
    TCompiledQuery Compile(EntityPredicate predicate);
}

/// <summary>
/// Compiler helpers.
/// </summary>
public static class QueryCompilerExtensions
{
    /// <summary>
    /// Validates the predicate against compiler capabilities before compiling it.
    /// </summary>
    public static TCompiledQuery CompileSupported<TCompiledQuery>(this IQueryCompiler<TCompiledQuery> compiler, EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(predicate);
        compiler.Capabilities.EnsureSupports(
            QueryCapabilityInspector.GetRequiredCapabilities(predicate).Value,
            operation: $"compile predicate with '{compiler.GetType().Name}'"
            );
        return compiler.Compile(predicate);
    }
}

/// <summary>
/// Computes the capability requirements of a structured predicate.
/// </summary>
public static class QueryCapabilityInspector
{
    /// <summary>
    /// Returns the capabilities required to execute the supplied predicate.
    /// </summary>
    public static QueryCapabilitySet GetRequiredCapabilities(EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var capabilities = QueryCapabilitySet.None;
        if (predicate.Scope is not null)
            capabilities = capabilities.With(QueryCapability.ScopedFields);

        return VisitFieldPredicate(predicate.Predicate, capabilities);
    }

    static QueryCapabilitySet VisitFieldPredicate(BoolExpr<FieldPredicate> expr, QueryCapabilitySet capabilities) => expr switch
    {
        Atom<FieldPredicate> atom => VisitFieldPredicate(atom.Term, capabilities),
        And<FieldPredicate> conjunction => conjunction.Terms.Aggregate(capabilities, static (current, term) => VisitFieldPredicate(term, current)),
        Or<FieldPredicate> disjunction => disjunction.Terms.Aggregate(capabilities, static (current, term) => VisitFieldPredicate(term, current)),
        Not<FieldPredicate> negation => VisitFieldPredicate(negation.Term, capabilities.With(QueryCapability.Negation)),
        _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
    };

    static QueryCapabilitySet VisitFieldPredicate(FieldPredicate predicate, QueryCapabilitySet capabilities) =>
        VisitValuePredicate(predicate.Predicate, capabilities);

    static QueryCapabilitySet VisitValuePredicate(BoolExpr<ValuePredicate> expr, QueryCapabilitySet capabilities) => expr switch
    {
        Atom<ValuePredicate> atom => VisitValuePredicate(atom.Term, capabilities),
        And<ValuePredicate> conjunction => conjunction.Terms.Aggregate(capabilities, static (current, term) => VisitValuePredicate(term, current)),
        Or<ValuePredicate> disjunction => disjunction.Terms.Aggregate(capabilities, static (current, term) => VisitValuePredicate(term, current)),
        Not<ValuePredicate> negation => VisitValuePredicate(negation.Term, capabilities.With(QueryCapability.Negation)),
        _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
    };

    static QueryCapabilitySet VisitValuePredicate(ValuePredicate predicate, QueryCapabilitySet capabilities)
    {
        if (predicate is AnyValuePredicate anyValue)
            return VisitValuePredicate(anyValue.Predicate, capabilities.With(QueryCapability.NestedAny));

        if (predicate is AnyFieldPredicate anyField)
            return VisitFieldPredicate(anyField.Predicate, capabilities.With(QueryCapability.NestedAny));

        var updated = capabilities.With(GetCapability(predicate));
        return RequiresCaseInsensitiveStringComparison(predicate)
            ? updated.With(QueryCapability.CaseInsensitiveStringComparison)
            : updated;
    }

    static bool RequiresCaseInsensitiveStringComparison(ValuePredicate predicate) => predicate switch
    {
        ExactValuePredicate { CaseSensitive: false } => true,
        PrefixValuePredicate { CaseSensitive: false } => true,
        SuffixValuePredicate { CaseSensitive: false } => true,
        ContainsValuePredicate { CaseSensitive: false } => true,
        _ => false
    };

    static QueryCapability GetCapability(ValuePredicate predicate) => predicate switch
    {
        ExactValuePredicate => QueryCapability.Equality,
        BoolValuePredicate => QueryCapability.Equality,
        IntValuePredicate => QueryCapability.Equality,
        LongValuePredicate => QueryCapability.Equality,
        DoubleValuePredicate => QueryCapability.Equality,
        DecimalValuePredicate => QueryCapability.Equality,
        DateValuePredicate => QueryCapability.Equality,
        PrefixValuePredicate => QueryCapability.Prefix,
        SuffixValuePredicate => QueryCapability.Suffix,
        ContainsValuePredicate => QueryCapability.Contains,
        FullTextValuePredicate => QueryCapability.FullText,
        ExistsValuePredicate => QueryCapability.Exists,
        DateRangeValuePredicate => QueryCapability.DateRange,
        NumberRangeValuePredicate => QueryCapability.NumberRange,
        InValuePredicate => QueryCapability.SetMembership,
        AnyValuePredicate => QueryCapability.NestedAny,
        AnyFieldPredicate => QueryCapability.NestedAny,
        GeoDistanceValuePredicate => QueryCapability.GeoDistance,
        _ => throw new InvalidOperationException($"Unknown value-predicate type '{predicate.GetType().Name}'.")
    };
}
