using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Cli;

/// <summary>An explicit option declaration producing the same metadata as typed configuration authoring.</summary>
/// <remarks>Declarations contain data only. Defaults are captured when authored; environment values are read
/// per invocation. Explicit collection declarations currently support string arrays only.</remarks>
public sealed record CliOption
{
    /// <summary>Gets the declared value type used by binding and checked retrieval.</summary>
    public Type ValueType { get; }
    // Raw default entries are owned snapshots, applied only when every higher-priority source is absent.
    internal ImmutableArray<KeyValuePair<string, string?>> DefaultEntries { get; init; } = [];
    /// <summary>Gets the explicit environment variable read per invocation, or null.</summary>
    public string? EnvironmentVariable { get; init; }
    /// <summary>Gets the option help text, or null.</summary>
    public string? Description { get; init; }

    /// <summary>Gets whether a value must be supplied by defaults or a configuration source.</summary>
    public bool Required { get; init; }

    /// <summary>Gets an optional short alias, including its leading dash.</summary>
    public string? ShortName { get; init; }

    /// <summary>Gets the allowed raw values, matched ignoring case.</summary>
    public ImmutableArray<string> AllowedValues { get; init; } = [];

    CliOption(Type valueType) => ValueType = valueType;

    /// <summary>Declares an option without a default.</summary>
    /// <typeparam name="T">Scalar value type or string array.</typeparam>
    /// <param name="description">Help text, or null.</param>
    /// <param name="environmentVariable">Explicit environment variable name, or null.</param>
    /// <returns>An immutable option declaration.</returns>
    /// <exception cref="ArgumentException">The collection type is unsupported or the environment name is empty.</exception>
    public static CliOption For<T>(string? description = null, string? environmentVariable = null)
    {
        if (typeof(T) != typeof(string) && typeof(T) != typeof(string[]) &&
            typeof(System.Collections.IEnumerable).IsAssignableFrom(typeof(T)))
            throw new ArgumentException("Explicit collection options currently support string[] only.");
        if (environmentVariable is not null && string.IsNullOrWhiteSpace(environmentVariable))
            throw new ArgumentException("Environment variable names cannot be empty.", nameof(environmentVariable));
        return new(typeof(T)) { Description = description, EnvironmentVariable = environmentVariable };
    }

    /// <summary>Declares an option with a captured default.</summary>
    /// <typeparam name="T">Scalar value type or string array.</typeparam>
    /// <param name="defaultValue">Default applied before registered configuration, environment, and CLI sources.</param>
    /// <param name="description">Help text, or null.</param>
    /// <param name="environmentVariable">Explicit environment variable name, or null.</param>
    /// <returns>An immutable declaration with a defensive snapshot of its default.</returns>
    /// <exception cref="ArgumentException">The collection type is unsupported or the environment name is empty.</exception>
    public static CliOption For<T>(T defaultValue, string? description = null, string? environmentVariable = null)
    {
        var option = For<T>(description: description, environmentVariable: environmentVariable);
        ImmutableArray<KeyValuePair<string, string?>> entries;
        if (defaultValue is string[] strings)
        {
            var builder = ImmutableArray.CreateBuilder<KeyValuePair<string, string?>>(strings.Length);
            for (var i = 0; i < strings.Length; i++)
                builder.Add(new($":{i}", strings[i]));
            entries = builder.MoveToImmutable();
        }
        else
        {
            var value = defaultValue is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : defaultValue?.ToString();
            entries = [new(string.Empty, value)];
        }
        return option with { DefaultEntries = entries };
    }

    internal static CliCommandBuilder<CliValues> CreateCommand(
        string name, string? description, IReadOnlyDictionary<string, CliOption> options,
        Action<CliCommandNode> applyPipelines, bool isRoot = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        Dictionary<string, CliOption> snapshot = new(StringComparer.OrdinalIgnoreCase);
        var descriptors = new ConfigurationParameterDescriptor[options.Count];
        var index = 0;
        HashSet<string> symbols = new(StringComparer.Ordinal);
        foreach (var (key, option) in options)
        {
            if (string.IsNullOrWhiteSpace(key) || key.StartsWith('-') || key.Any(char.IsWhiteSpace) || key.Contains(':'))
                throw new ArgumentException($"Invalid option key '{key}'; use an unprefixed flat name.", nameof(options));
            ArgumentNullException.ThrowIfNull(option);
            if (option.EnvironmentVariable is not null && string.IsNullOrWhiteSpace(option.EnvironmentVariable))
                throw new ArgumentException($"Option '{key}' has an empty environment variable name.", nameof(options));
            if (!snapshot.TryAdd(key, option))
                throw new ArgumentException($"Duplicate option key '{key}'.", nameof(options));
            if (!symbols.Add($"--{key}"))
                throw new ArgumentException($"Duplicate option name '--{key}'.", nameof(options));
            if (option.ShortName is { } alias &&
                (!alias.StartsWith('-') || alias.Any(char.IsWhiteSpace) || alias.Length < 2 || !symbols.Add(alias)))
                throw new ArgumentException($"Invalid or duplicate alias '{alias}'.", nameof(options));
            descriptors[index++] = new(key, new([FieldPathSegment.ForField(key)]), key, $"--{key}",
                option.ShortName, option.Description, option.AllowedValues, option.Required, null, option.ValueType);
        }

        var command = new CliCommandBuilder<CliValues>(name, description, applyPipelines)
        {
            IsRoot = isRoot,
            ExplicitDescriptors = descriptors,
            ExplicitParser = configuration => new CliValues(
                ConfigurationParameterParser.ParseValues(configuration, descriptors), descriptors),
            ApplyDefaults = supplied =>
            {
                // Defaults are fallback values for the whole parameter. In particular a shorter CLI
                // collection must not inherit trailing elements from a default collection.
                Dictionary<string, string?> defaults = new(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, option) in snapshot)
                {
                    var section = supplied.GetSection(key);
                    if (section.Value is not null || section.GetChildren().Any())
                        continue;
                    foreach (var entry in option.DefaultEntries)
                        defaults.Add(key + entry.Key, entry.Value);
                }
                return defaults.Count == 0 ? supplied : new ConfigurationBuilder()
                    .AddInMemoryCollection(defaults).AddConfiguration(supplied).Build();
            },
            ConfigureEnvironmentMappings = builder => builder.AddInMemoryCollection(
                snapshot.Where(pair => pair.Value.EnvironmentVariable is not null)
                    .Select(pair => new KeyValuePair<string, string?>(pair.Key,
                        Environment.GetEnvironmentVariable(pair.Value.EnvironmentVariable!)))
                    .Where(pair => pair.Value is not null))
        };
        applyPipelines(command);
        return command;
    }
}
