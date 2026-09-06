namespace Cohesive.Adapters.Sql;

/// <summary>Deterministic whitespace policy for SQL construction, independent of target grammar.</summary>
public enum SqlFormatting
{
    /// <summary>Single-space clause/list separators; the existing compact rendering contract.</summary>
    Compact,
    /// <summary>LF line endings, four-space nesting, separate clauses and one SELECT/order item per line.</summary>
    /// <remarks>Renders directly from the SQL tree without rewriting expressions, identifiers or parameter slots.</remarks>
    Indented
}
