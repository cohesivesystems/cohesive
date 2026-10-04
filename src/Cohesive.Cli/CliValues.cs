using System.Collections.Immutable;
using Cohesive.Configuration;

namespace Cohesive.Cli;

/// <summary>Invocation values bound from explicit declarations, with checked typed access.</summary>
public sealed class CliValues
{
    readonly IReadOnlyDictionary<string, object?> values;
    readonly Dictionary<string, Type> types;

    internal CliValues(IReadOnlyDictionary<string, object?> values, IReadOnlyList<ConfigurationParameterDescriptor> declarations)
    {
        this.values = values;
        types = declarations.ToDictionary(parameter => parameter.ConfigurationKey,
            parameter => parameter.ParameterType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads a declared value using its exact declared type.</summary>
    /// <typeparam name="T">The declared option type.</typeparam>
    /// <param name="name">Unprefixed option name, matched ignoring case.</param>
    /// <returns>The parsed value, or the type's default when an optional value is absent.
    /// String arrays are copied so callers cannot mutate invocation storage.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="KeyNotFoundException">The option name was not declared.</exception>
    /// <exception cref="InvalidOperationException">The requested type differs from the declaration.</exception>
    public T? Get<T>(string name)
    {
        if (!types.TryGetValue(name, out var type))
            throw new KeyNotFoundException($"Option '{name}' was not declared.");
        if (type != typeof(T))
            throw new InvalidOperationException($"Option '{name}' is declared as '{type.Name}', not '{typeof(T).Name}'.");
        if (!values.TryGetValue(name, out var value))
            return default;
        if (type == typeof(string[]) && value is ImmutableArray<string> collection)
            return (T)(object)collection.ToArray();
        return (T?)value;
    }
}
