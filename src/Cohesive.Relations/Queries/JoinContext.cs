using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Queries;

/// <summary>
/// Rooted join context used by join and projection expressions.
/// </summary>
public sealed class JoinContext(Observation root, ShapeMappingContext? mappingContext = null)
{
    readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);
    readonly ShapeMappingContext mapping = mappingContext ?? ShapeMappingContext.Default;

    /// <summary>
    /// Root observation being projected.
    /// </summary>
    public Observation Root { get; } = Guard.RequireNotNull(root);

    /// <summary>
    /// Maps the root observation to a CLR type.
    /// </summary>
    public T RootAs<T>() => Map<T>(Root);

    internal void SetOne(string alias, Observation? value) =>
        values[Guard.RequireNotNullOrWhiteSpace(alias)] = value;

    internal void SetMany(string alias, IReadOnlyList<Observation> items) =>
        values[Guard.RequireNotNullOrWhiteSpace(alias)] = Guard.RequireNotNull(items);

    /// <summary>
    /// Returns a single joined observation or <see langword="null" /> when no value was hydrated.
    /// </summary>
    public Observation? One(string alias)
    {
        if (!values.TryGetValue(Guard.RequireNotNullOrWhiteSpace(alias), out var value) || value is null)
            return null;

        return value as Observation
            ?? throw new InvalidOperationException($"Join alias '{alias}' expected a single observation value but resolved '{value.GetType().FullName}'.");
    }

    /// <summary>
    /// Returns a required single joined observation.
    /// </summary>
    public Observation RequireOne(string alias) =>
        One(alias)
        ?? throw new InvalidOperationException($"Join alias '{alias}' is required but no value was hydrated.");

    /// <summary>
    /// Returns a single joined value mapped to <typeparamref name="T" />, or <see langword="default" /> when no value was hydrated.
    /// </summary>
    public T? One<T>(string alias)
    {
        var value = One(alias);
        return value is null ? default : Map<T>(value);
    }

    /// <summary>
    /// Returns a required single joined value mapped to <typeparamref name="T" />.
    /// </summary>
    public T RequireOne<T>(string alias)
    {
        var value = One<T>(alias);
        if (value is null)
            throw new InvalidOperationException($"Join alias '{alias}' is required but no value was hydrated.");

        return value;
    }

    /// <summary>
    /// Returns a collection join as raw observations.
    /// </summary>
    public IReadOnlyList<Observation> Many(string alias)
    {
        if (!values.TryGetValue(Guard.RequireNotNullOrWhiteSpace(alias), out var value) || value is null)
            return [];

        return value as IReadOnlyList<Observation>
            ?? throw new InvalidOperationException($"Join alias '{alias}' expected a collection of observations but resolved '{value.GetType().FullName}'.");
    }

    /// <summary>
    /// Returns a collection join mapped to <typeparamref name="T" />.
    /// </summary>
    public IReadOnlyList<T> Many<T>(string alias)
    {
        var observations = Many(alias);
        if (observations.Count == 0)
            return [];

        if (typeof(T) == typeof(Observation))
            return observations.Cast<T>().ToArray();

        return observations.Select(Map<T>).ToArray();
    }

    T Map<T>(Observation observation)
    {
        if (typeof(T) == typeof(Observation))
            return (T)(object)observation;

        return mapping.Map<T>(observation);
    }
}
