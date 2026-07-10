namespace Cohesive.Relations.Queries;

/// <summary>Represents a query source.</summary>
public sealed record QuerySource
{
    /// <summary>
    /// Creates a named query source.
    /// </summary>
    public QuerySource(string name) => 
        Name = Guard.RequireNotNullOrWhiteSpace(name);

    /// <summary>
    /// Human-readable source name used for registry lookup and diagnostics.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Creates a named query source.
    /// </summary>
    public static QuerySource For(string name) => new(name);

    /// <summary>
    /// Creates a named query source for the supplied CLR projection type.
    /// </summary>
    public static QuerySource For<TRecord>(string? name = null) =>
        new(name ?? typeof(TRecord).FullName ?? typeof(TRecord).Name);
}
