namespace Cohesive.Model;

/// <summary>
/// Built-in <see cref="Expr.Call(string,Expr[])"/> function names recognized by one or more Cohesive expression evaluators.
/// </summary>
/// <remarks>
/// Support varies by evaluator. Custom function names may still be authored directly with <see cref="Expr.Call(string,Expr[])"/>.
/// </remarks>
public static class ExprFunctionNames
{
    /// <summary>
    /// Function name for testing whether all values in a sequence evaluate to true.
    /// </summary>
    public const string All = "all";

    /// <summary>
    /// Function name for collection existential semantics.
    /// </summary>
    /// <remarks>
    /// The one-argument form tests Boolean collection elements directly. The two-argument form evaluates its
    /// Boolean predicate in a current-item scope for each collection element and preserves same-element correlation
    /// across every predicate field read. Canonical execution rejects missing, null, and non-array collection values.
    /// In the two-argument form it also rejects missing, null, and non-Boolean predicate results rather than coercing
    /// them to <see langword="false"/>.
    /// </remarks>
    public const string Any = "any";

    /// <summary>
    /// Function name for returning a collection with a single item appended.
    /// </summary>
    public const string Append = "append";

    /// <summary>
    /// Function name for returning a collection with a range of items appended.
    /// </summary>
    public const string AppendRange = "appendRange";

    /// <summary>
    /// Function name for computing the numeric average of values in a sequence in the canonical decimal result domain.
    /// </summary>
    public const string Avg = "avg";

    /// <summary>
    /// Function name for concatenating string arguments.
    /// </summary>
    public const string Concat = "concat";

    /// <summary>
    /// Function name for testing whether a sequence contains a value.
    /// </summary>
    public const string Contains = "contains";

    /// <summary>
    /// Function name for counting elements, properties, or scalar values in an expression result.
    /// </summary>
    public const string Count = "count";

    /// <summary>
    /// Function name for returning the current entity or logical entity identifier.
    /// </summary>
    public const string EntityId = "entityId";

    /// <summary>
    /// Function name for testing whether one text value ends with another using ordinal, case-sensitive semantics.
    /// </summary>
    public const string EndsWith = "endsWith";

    /// <summary>
    /// Function name for testing whether one text value starts with another using ordinal, case-sensitive semantics.
    /// </summary>
    public const string StartsWith = "startsWith";

    /// <summary>
    /// Function name for testing whether one text value contains another using ordinal, case-sensitive semantics.
    /// </summary>
    /// <remarks>This is distinct from <see cref="Contains"/>, which represents collection membership.</remarks>
    public const string TextContains = "textContains";

    /// <summary>
    /// Function name for grouping source items into an object keyed by a selector expression.
    /// </summary>
    public const string GroupBy = "groupBy";

    /// <summary>
    /// Function name for grouping source items into row objects that expose each group key and its items.
    /// </summary>
    public const string GroupByRows = "groupByRows";

    /// <summary>
    /// Function name for returning a collection with a single item inserted at a requested index.
    /// </summary>
    public const string InsertAt = "insertAt";

    /// <summary>
    /// Function name for returning a collection with a range of items inserted at a requested index.
    /// </summary>
    public const string InsertRangeAt = "insertRangeAt";

    /// <summary>
    /// Function name for filtering a right-side collection to items whose key matches the left-side key.
    /// </summary>
    public const string Join = "join";

    /// <summary>
    /// Function name for returning the current root observation key.
    /// </summary>
    public const string Key = "key";

    /// <summary>
    /// Function name for computing the maximum numeric value in a sequence.
    /// </summary>
    public const string Max = "max";

    /// <summary>
    /// Function name for computing the minimum numeric value in a sequence.
    /// </summary>
    public const string Min = "min";

    /// <summary>
    /// Function name for creating an object from alternating key (expressed as string constants) and value arguments.
    /// </summary>
    public const string Object = "object";

    /// <summary>
    /// Function name for projecting each item of a source sequence (first parameter) through a selector expression (second parameter).
    /// </summary>
    public const string Select = "select";

    /// <summary>
    /// Function name for returning the current relation source set as row objects.
    /// </summary>
    public const string SourceRows = "sourceRows";

    /// <summary>
    /// Function name for computing the numeric sum of values in a sequence.
    /// </summary>
    public const string Sum = "sum";
}
