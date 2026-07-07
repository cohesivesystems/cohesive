using System.Linq.Expressions;

namespace Cohesive.Configuration;

/// <summary>
/// Projects values from a source object into hierarchical configuration overrides for a target settings type.
/// </summary>
/// <typeparam name="TSource">The source object type.</typeparam>
/// <typeparam name="TTarget">The target settings type.</typeparam>
public sealed class ConfigurationProjection<TSource, TTarget>(string? prefix = null, char separator = ':')
{
    readonly List<Action<TSource, ConfigurationParameterOverrides<TTarget>>> steps = [];

    /// <summary>
    /// Maps a source-derived value onto one or more target paths.
    /// </summary>
    /// <param name="valueSelector">Selects the source value to write.</param>
    /// <param name="targets">Target paths to receive the value.</param>
    /// <returns>The current projection.</returns>
    public ConfigurationProjection<TSource, TTarget> Map(Func<TSource, object?> valueSelector, params Expression<Func<TTarget, object?>>[] targets)
    {
        ArgumentNullException.ThrowIfNull(valueSelector);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Length == 0)
            throw new ArgumentException("At least one target path is required.", nameof(targets));

        steps.Add((source, builder) =>
        {
            var value = valueSelector(source);
            foreach (var target in targets)
                builder.Add(target, value);
        });
        return this;
    }

    /// <summary>
    /// Maps a source-derived value onto one or more raw target configuration paths.
    /// </summary>
    /// <param name="valueSelector">Selects the source value to write.</param>
    /// <param name="paths">Configuration-space target paths.</param>
    /// <returns>The current projection.</returns>
    public ConfigurationProjection<TSource, TTarget> Map(Func<TSource, object?> valueSelector, params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(valueSelector);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0)
            throw new ArgumentException("At least one target path is required.", nameof(paths));

        steps.Add((source, builder) =>
        {
            var value = valueSelector(source);
            foreach (var path in paths)
                builder.Add(path, value);
        });
        return this;
    }

    /// <summary>
    /// Writes a constant value to one or more target paths.
    /// </summary>
    /// <param name="value">Constant value to write.</param>
    /// <param name="targets">Target paths to receive the value.</param>
    /// <returns>The current projection.</returns>
    public ConfigurationProjection<TSource, TTarget> Set(object? value, params Expression<Func<TTarget, object?>>[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Length == 0)
            throw new ArgumentException("At least one target path is required.", nameof(targets));

        steps.Add((_, builder) =>
        {
            foreach (var target in targets)
                builder.Add(target, value);
        });
        return this;
    }

    /// <summary>
    /// Writes a constant value to one or more raw target configuration paths.
    /// </summary>
    /// <param name="value">Constant value to write.</param>
    /// <param name="paths">Configuration-space target paths.</param>
    /// <returns>The current projection.</returns>
    public ConfigurationProjection<TSource, TTarget> Set(object? value, params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0)
            throw new ArgumentException("At least one target path is required.", nameof(paths));

        steps.Add((_, builder) =>
        {
            foreach (var path in paths)
                builder.Add(path, value);
        });
        return this;
    }

    /// <summary>
    /// Applies a nested projection only when the predicate matches the source object.
    /// </summary>
    /// <param name="predicate">Condition that enables the nested projection.</param>
    /// <param name="configure">Nested projection configuration.</param>
    /// <returns>The current projection.</returns>
    public ConfigurationProjection<TSource, TTarget> When(Func<TSource, bool> predicate, Action<ConfigurationProjection<TSource, TTarget>> configure)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(configure);

        var conditional = new ConfigurationProjection<TSource, TTarget>(separator: separator);
        configure(conditional);

        steps.Add((source, builder) =>
        {
            if (predicate(source))
                conditional.Apply(source, builder);
        });
        return this;
    }

    /// <summary>
    /// Builds configuration overrides for the supplied source object.
    /// </summary>
    /// <param name="source">Source object to project from.</param>
    /// <returns>Configuration overrides for the target settings type.</returns>
    public IReadOnlyDictionary<string, string?> Build(TSource source)
    {
        var builder = new ConfigurationParameterOverrides<TTarget>(prefix: prefix, separator: separator);
        Apply(source, builder);
        return builder.Overrides;
    }

    void Apply(TSource source, ConfigurationParameterOverrides<TTarget> builder)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var step in steps)
            step(source, builder);
    }
}
