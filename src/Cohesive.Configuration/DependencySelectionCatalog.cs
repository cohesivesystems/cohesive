namespace Cohesive.Configuration;

/// <summary>
/// Describes one named dependency selection rule.
/// </summary>
/// <typeparam name="TRequest">Semantic request type used to select a dependency.</typeparam>
/// <typeparam name="TDependency">Dependency type produced by the selected rule.</typeparam>
public sealed class DependencySelectionRule<TRequest, TDependency>
{
    /// <summary>
    /// Creates one named dependency selection rule.
    /// </summary>
    /// <param name="name">Stable rule name used in diagnostics.</param>
    /// <param name="matches">Predicate that determines whether this rule applies to a request.</param>
    /// <param name="create">Factory that creates the dependency for a matching request.</param>
    /// <param name="priority">Rule priority. Higher values win when multiple rules match.</param>
    public DependencySelectionRule(
        string name,
        Func<TRequest, bool> matches,
        Func<TRequest, TDependency> create,
        int priority = 0
        )
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Matches = Guard.RequireNotNull(matches);
        Create = Guard.RequireNotNull(create);
        Priority = priority;
    }

    /// <summary>
    /// Stable rule name used in diagnostics.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Predicate that determines whether this rule applies to a request.
    /// </summary>
    public Func<TRequest, bool> Matches { get; }

    /// <summary>
    /// Factory that creates the dependency for a matching request.
    /// </summary>
    public Func<TRequest, TDependency> Create { get; }

    /// <summary>
    /// Rule priority. Higher values win when multiple rules match.
    /// </summary>
    public int Priority { get; }
}

/// <summary>
/// Result of resolving one dependency selection request.
/// </summary>
/// <param name="RuleName">Name of the rule that produced the dependency.</param>
/// <param name="Dependency">Selected dependency value.</param>
/// <typeparam name="TDependency">Dependency type produced by the selected rule.</typeparam>
public sealed record DependencySelectionResult<TDependency>(
    string RuleName,
    TDependency Dependency
    );

/// <summary>
/// Resolves dependencies from named, prioritized semantic selection rules.
/// </summary>
/// <typeparam name="TRequest">Semantic request type used to select a dependency.</typeparam>
/// <typeparam name="TDependency">Dependency type produced by the selected rule.</typeparam>
public sealed class DependencySelectionCatalog<TRequest, TDependency>
{
    readonly IReadOnlyList<DependencySelectionRule<TRequest, TDependency>> rules;

    /// <summary>
    /// Creates a dependency selection catalog from ordered rules.
    /// </summary>
    /// <param name="rules">Named selection rules. Higher priority rules win over lower priority rules.</param>
    public DependencySelectionCatalog(IReadOnlyList<DependencySelectionRule<TRequest, TDependency>> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
            throw new ArgumentException("At least one dependency selection rule is required.", nameof(rules));

        var duplicateName = rules
            .GroupBy(static rule => rule.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .FirstOrDefault();
        if (duplicateName is not null)
            throw new ArgumentException($"Duplicate dependency selection rule '{duplicateName}'.", nameof(rules));

        this.rules = [..rules];
    }

    /// <summary>
    /// Resolves the selected dependency for a request.
    /// </summary>
    /// <param name="request">Semantic request to match against the registered rules.</param>
    /// <exception cref="InvalidOperationException">No dependency selection rule matched the supplied request.</exception>
    /// <exception cref="InvalidOperationException">Ambiguous dependency selection rules matched the supplied request.</exception>
    public DependencySelectionResult<TDependency> Resolve(TRequest request)
    {
        if (TryResolve(request, out var result) && result is not null)
            return result;

        throw new InvalidOperationException("No dependency selection rule matched the supplied request.");
    }

    /// <summary>
    /// Attempts to resolve the selected dependency for a request.
    /// </summary>
    /// <param name="request">Semantic request to match against the registered rules.</param>
    /// <param name="result">Selected dependency result when a rule matches.</param>
    /// <exception cref="InvalidOperationException">Ambiguous dependency selection rules matched the supplied request.</exception>
    public bool TryResolve(TRequest request, out DependencySelectionResult<TDependency>? result)
    {
        DependencySelectionRule<TRequest, TDependency>? selected = null;
        List<DependencySelectionRule<TRequest, TDependency>>? ambiguous = null;

        foreach (var rule in rules)
        {
            if (!rule.Matches(request))
                continue;

            if (selected is null || rule.Priority > selected.Priority)
            {
                selected = rule;
                ambiguous = null;
                continue;
            }

            if (rule.Priority == selected.Priority)
            {
                ambiguous ??= [selected];
                ambiguous.Add(rule);
            }
        }

        if (ambiguous is not null)
            throw new InvalidOperationException($"Dependency selection rules with the same priority matched the supplied request: {string.Join(", ", ambiguous.Select(static rule => rule.Name))}.");

        if (selected is null)
        {
            result = null;
            return false;
        }

        result = new(selected.Name, selected.Create(request));
        return true;
    }
}

/// <summary>
/// Builds a dependency selection catalog.
/// </summary>
/// <typeparam name="TRequest">Semantic request type used to select a dependency.</typeparam>
/// <typeparam name="TDependency">Dependency type produced by the selected rule.</typeparam>
public sealed class DependencySelectionCatalogBuilder<TRequest, TDependency>
{
    readonly List<DependencySelectionRule<TRequest, TDependency>> rules = [];

    /// <summary>
    /// Adds a named dependency selection rule.
    /// </summary>
    /// <param name="name">Stable rule name used in diagnostics.</param>
    /// <param name="matches">Predicate that determines whether this rule applies to a request.</param>
    /// <param name="create">Factory that creates the dependency for a matching request.</param>
    /// <param name="priority">Rule priority. Higher values win when multiple rules match.</param>
    public DependencySelectionCatalogBuilder<TRequest, TDependency> Add(
        string name,
        Func<TRequest, bool> matches,
        Func<TRequest, TDependency> create,
        int priority = 0
        )
    {
        rules.Add(new(name, matches, create, priority));
        return this;
    }

    /// <summary>
    /// Adds a fallback rule that matches every request at the lowest priority.
    /// </summary>
    /// <param name="name">Stable rule name used in diagnostics.</param>
    /// <param name="create">Factory that creates the fallback dependency.</param>
    public DependencySelectionCatalogBuilder<TRequest, TDependency> AddFallback(
        string name,
        Func<TRequest, TDependency> create
        )
    {
        ArgumentNullException.ThrowIfNull(create);
        return Add(name, static _ => true, create, priority: int.MinValue);
    }

    /// <summary>
    /// Builds the dependency selection catalog.
    /// </summary>
    public DependencySelectionCatalog<TRequest, TDependency> Build() => new(rules);
}
