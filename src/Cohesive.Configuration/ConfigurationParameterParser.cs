using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Cohesive.Model;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Configuration;

/// <summary>
/// Parses hierarchical configuration values from <see cref="IConfiguration"/> into typed objects using
/// attribute metadata and expression-based overrides.
/// </summary>
/// <remarks>
/// The parser resolves raw values from configuration space, validates them against the merged parameter
/// metadata, converts them into binder-friendly canonical values, and then delegates final object creation
/// to the standard Microsoft configuration binder.
/// </remarks>
public static class ConfigurationParameterParser
{
    /// <summary>
    /// Parses a typed configuration object from an existing <see cref="IConfiguration"/> graph.
    /// </summary>
    /// <param name="configuration">Configuration graph to read from.</param>
    /// <param name="options">Optional expression-based overrides and enum mappings.</param>
    /// <typeparam name="T">Root typed configuration object.</typeparam>
    /// <returns>The parsed and bound configuration object.</returns>
    /// <exception cref="ConfigurationParameterParseException">Thrown when required values are missing or raw values cannot be validated or converted.</exception>
    /// <remarks>
    /// Property metadata is resolved from the CLR object graph, then merged with
    /// <see cref="ConfigurationParameterAttribute"/> declarations and any matching overrides from
    /// <paramref name="options"/>. Expression-based overrides take precedence over attribute metadata.
    /// </remarks>
    public static T Parse<T>(IConfiguration configuration, ConfigurationParameterOptions<T>? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var parameters = BuildMetadata(options);
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        List<string> errors = [];

        foreach (var parameter in parameters)
        {
            if (TryReadStringCollectionValues(configuration, parameter, out var collectionValues))
            {
                if (collectionValues.Length == 0)
                {
                    if (parameter.Required)
                        errors.Add($"Missing required configuration value '{parameter.ConfigurationKey}' for '{parameter.Path}'.");

                    continue;
                }

                if (parameter.AllowedValues.Length > 0)
                {
                    var invalidValues = collectionValues
                        .Where(value => !parameter.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    foreach (var invalidValue in invalidValues)
                    {
                        errors.Add($"Configuration value '{parameter.ConfigurationKey}' for '{parameter.Path}' must contain only values from [{string.Join(", ", parameter.AllowedValues)}], but included '{invalidValue}'.");
                    }

                    if (invalidValues.Length > 0)
                        continue;
                }

                for (var i = 0; i < collectionValues.Length; i++)
                    values[$"{parameter.CanonicalKey}:{i}"] = collectionValues[i];

                continue;
            }

            var rawValue = configuration[parameter.ConfigurationKey];
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                if (parameter.Required)
                    errors.Add($"Missing required configuration value '{parameter.ConfigurationKey}' for '{parameter.Path}'.");
                continue;
            }

            if (parameter.AllowedValues.Length > 0 && !parameter.AllowedValues.Contains(rawValue, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Configuration value '{parameter.ConfigurationKey}' for '{parameter.Path}' must be one of [{string.Join(", ", parameter.AllowedValues)}], but was '{rawValue}'.");
                continue;
            }

            if (!TryConvertValue(rawValue, parameter, options, out var convertedValue, out var error))
            {
                errors.Add(error!);
                continue;
            }

            values[parameter.CanonicalKey] = convertedValue is null
                ? null
                : FormatForBinder(convertedValue, parameter.ParameterType);
        }

        if (errors.Count > 0)
            throw new ConfigurationParameterParseException("Configuration parameters were invalid.", errors);

        try
        {
            var canonicalConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            var parsed = canonicalConfiguration.Get<T>(binderOptions => binderOptions.BindNonPublicProperties = true);
            if (parsed is not null)
                return parsed;

            return Activator.CreateInstance<T>();
        }
        catch (ConfigurationParameterParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConfigurationParameterParseException(
                $"Configuration binding failed for type '{typeof(T).FullName}'.",
                errors: [$"Binding failed: {ex.Message}"]
                );
        }
    }

    /// <summary>
    /// Builds an <see cref="IConfiguration"/> pipeline from custom sources, environment variables, and CLI arguments,
    /// then parses the result into a typed configuration object.
    /// </summary>
    /// <param name="args">CLI arguments to apply through generated switch mappings.</param>
    /// <param name="configure">Optional callback to add configuration providers such as JSON files.</param>
    /// <param name="options">Optional expression-based overrides and enum mappings.</param>
    /// <param name="environmentVariablePrefix">Optional prefix filter for environment variables.</param>
    /// <typeparam name="T">Root typed configuration object.</typeparam>
    /// <returns>The parsed and bound configuration object.</returns>
    /// <exception cref="ConfigurationParameterParseException">Thrown when required values are missing or raw values cannot be validated or converted.</exception>
    /// <remarks>
    /// Providers are applied in the order established by <see cref="BuildConfiguration{T}"/>, so CLI arguments
    /// override environment variables and environment variables override any custom providers added through
    /// <paramref name="configure"/>.
    /// </remarks>
    public static T Parse<T>(IReadOnlyList<string> args, Action<IConfigurationBuilder>? configure = null, ConfigurationParameterOptions<T>? options = null, string? environmentVariablePrefix = null)
    {
        var configuration = BuildConfiguration(args, configure, options, environmentVariablePrefix);
        return Parse(configuration, options);
    }

    /// <summary>
    /// Builds an <see cref="IConfigurationRoot"/> that applies custom providers first, then environment variables,
    /// then CLI arguments.
    /// </summary>
    /// <param name="args">CLI arguments to apply through generated switch mappings.</param>
    /// <param name="configure">Optional callback to add configuration providers such as JSON files.</param>
    /// <param name="options">Optional expression-based overrides and enum mappings used for CLI switch generation.</param>
    /// <param name="environmentVariablePrefix">Optional prefix filter for environment variables.</param>
    /// <typeparam name="T">Root typed configuration object.</typeparam>
    /// <returns>The composed configuration root.</returns>
    /// <remarks>
    /// The resulting pipeline applies custom providers first, then environment variables, then CLI arguments,
    /// matching the usual last-provider-wins behavior of <see cref="ConfigurationBuilder"/>.
    /// </remarks>
    public static IConfigurationRoot BuildConfiguration<T>(IReadOnlyList<string> args, Action<IConfigurationBuilder>? configure = null, ConfigurationParameterOptions<T>? options = null, string? environmentVariablePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parameters = BuildMetadata(options);
        var switchMappings = BuildSwitchMappings(parameters);
        var collectionSwitchMappings = BuildStringCollectionSwitchMappings(parameters);
        var nonCollectionSwitchMappings = switchMappings
            .Where(mapping => !collectionSwitchMappings.ContainsKey(mapping.Key))
            .ToDictionary(static mapping => mapping.Key, static mapping => mapping.Value, StringComparer.OrdinalIgnoreCase);
        var (remainingArgs, collectionValues) = ExtractStringCollectionCommandLineValues(args, collectionSwitchMappings);

        var builder = new ConfigurationBuilder();
        configure?.Invoke(builder);

        if (environmentVariablePrefix is null)
            builder.AddEnvironmentVariables();
        else
            builder.AddEnvironmentVariables(prefix: environmentVariablePrefix);

        builder.AddCommandLine([..remainingArgs], nonCollectionSwitchMappings);
        if (collectionValues.Count > 0)
            builder.AddInMemoryCollection(collectionValues);
        return builder.Build();
    }

    /// <summary>
    /// Describes the resolved parameter metadata for a configuration type.
    /// </summary>
    /// <param name="options">Optional expression-based overrides and enum mappings.</param>
    /// <typeparam name="T">Root typed configuration object.</typeparam>
    /// <returns>Resolved parameter descriptors for the configuration type.</returns>
    /// <remarks>
    /// This method is useful for generating help output or documentation because it exposes the effective
    /// configuration keys, CLI aliases, allowed values, and required flags after metadata merging.
    /// </remarks>
    public static IReadOnlyList<ConfigurationParameterDescriptor> Describe<T>(ConfigurationParameterOptions<T>? options = null) => BuildMetadata(options)
        .Select(parameter => new ConfigurationParameterDescriptor(
            PropertyName: parameter.PropertyName,
            Path: parameter.Path,
            ConfigurationKey: parameter.ConfigurationKey,
            CliName: parameter.CliName,
            CliShortName: parameter.CliShortName,
            Description: parameter.Description,
            AllowedValues: parameter.AllowedValues,
            Required: parameter.Required,
            TimeUnit: parameter.TimeUnit,
            ParameterType: parameter.ParameterType))
        .ToArray();

    static Dictionary<string, string> BuildSwitchMappings<T>(ConfigurationParameterOptions<T>? options) =>
        BuildSwitchMappings(BuildMetadata(options));

    static Dictionary<string, string> BuildSwitchMappings(IEnumerable<ConfigurationParameterMetadata> parameters)
    {
        Dictionary<string, string> mappings = new(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            AddSwitchMapping(mappings, cliName: parameter.CliName, configurationKey: parameter.ConfigurationKey, parameter.Path);
            if (parameter.CliShortName is not null)
                AddSwitchMapping(mappings, cliName: parameter.CliShortName, parameter.ConfigurationKey, parameter.Path);
        }
        return mappings;
    }

    static Dictionary<string, string> BuildStringCollectionSwitchMappings(IEnumerable<ConfigurationParameterMetadata> parameters)
    {
        Dictionary<string, string> mappings = new(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters.Where(parameter => IsStringCollectionType(parameter.ParameterType)))
        {
            AddSwitchMapping(mappings, cliName: parameter.CliName, configurationKey: parameter.ConfigurationKey, parameter.Path);
            if (parameter.CliShortName is not null)
                AddSwitchMapping(mappings, cliName: parameter.CliShortName, parameter.ConfigurationKey, parameter.Path);
        }

        return mappings;
    }

    static void AddSwitchMapping(IDictionary<string, string> mappings, string cliName, string configurationKey, FieldPath path)
    {
        if (mappings.TryGetValue(cliName, out var existing) && !string.Equals(existing, configurationKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"CLI switch '{cliName}' is mapped to both '{existing}' and '{configurationKey}'.");

        mappings[cliName] = configurationKey;
    }

    static (IReadOnlyList<string> RemainingArgs, Dictionary<string, string?> CollectionValues) ExtractStringCollectionCommandLineValues(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> collectionSwitchMappings)
    {
        if (collectionSwitchMappings.Count == 0)
            return (args, new(StringComparer.OrdinalIgnoreCase));

        List<string> remainingArgs = [];
        Dictionary<string, List<string>> collectedValues = new(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Count; i++)
        {
            var current = args[i];
            if (!TryMatchCollectionSwitch(current, collectionSwitchMappings, out var configurationKey, out var inlineValue))
            {
                remainingArgs.Add(current);
                continue;
            }

            if (inlineValue is not null)
            {
                AddStringCollectionValues(collectedValues, configurationKey, inlineValue);
                continue;
            }

            if (i + 1 >= args.Count || LooksLikeCliSwitch(args[i + 1]))
                continue;

            AddStringCollectionValues(collectedValues, configurationKey, args[i + 1]);
            i++;
        }

        Dictionary<string, string?> collectionValues = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (configurationKey, values) in collectedValues)
        {
            for (var i = 0; i < values.Count; i++)
                collectionValues[$"{configurationKey}:{i}"] = values[i];
        }

        return (remainingArgs, collectionValues);
    }

    static bool TryMatchCollectionSwitch(
        string argument,
        IReadOnlyDictionary<string, string> collectionSwitchMappings,
        out string configurationKey,
        out string? inlineValue)
    {
        if (collectionSwitchMappings.TryGetValue(argument, out var matchedConfigurationKey))
        {
            configurationKey = matchedConfigurationKey;
            inlineValue = null;
            return true;
        }

        var separatorIndex = argument.IndexOf('=');
        if (separatorIndex > 0
            && collectionSwitchMappings.TryGetValue(argument[..separatorIndex], out matchedConfigurationKey))
        {
            configurationKey = matchedConfigurationKey;
            inlineValue = argument[(separatorIndex + 1)..];
            return true;
        }

        configurationKey = string.Empty;
        inlineValue = null;
        return false;
    }

    static bool LooksLikeCliSwitch(string value) =>
        value.StartsWith("--", StringComparison.Ordinal)
        || (value.StartsWith("-", StringComparison.Ordinal) && value.Length > 1);

    static void AddStringCollectionValues(IDictionary<string, List<string>> collectedValues, string configurationKey, string rawValue)
    {
        if (!collectedValues.TryGetValue(configurationKey, out var values))
        {
            values = [];
            collectedValues[configurationKey] = values;
        }

        values.AddRange(ParseCommaSeparatedValues(rawValue));
    }

    static ImmutableArray<ConfigurationParameterMetadata> BuildMetadata<T>(ConfigurationParameterOptions<T>? options)
    {
        var optionLookup = (options?.Options ?? []).ToDictionary(option => option.Path, option => option);
        List<ConfigurationParameterMetadata> parameters = [];
        Traverse(
            currentType: typeof(T),
            propertyChain: [],
            canonicalSegments: [],
            configurationSegments: [],
            parameters,
            optionLookup,
            options
            );
        return [.. parameters];
    }

    static void Traverse<T>(
        Type currentType,
        ImmutableArray<PropertyInfo> propertyChain,
        ImmutableArray<string> canonicalSegments,
        ImmutableArray<string> configurationSegments,
        List<ConfigurationParameterMetadata> parameters,
        IReadOnlyDictionary<FieldPath, ConfigurationParameterOption> optionLookup,
        ConfigurationParameterOptions<T>? options
        )
    {
        foreach (var property in GetBindableProperties(currentType))
        {
            var nextPropertyChain = propertyChain.Add(property);
            var path = CreateFieldPath(nextPropertyChain);
            optionLookup.TryGetValue(path, out var propertyOption);

            var attribute = property.GetCustomAttribute<ConfigurationParameterAttribute>(inherit: true);
            var configurationKeyAttribute = property.GetCustomAttribute<ConfigurationKeyNameAttribute>(inherit: true);

            var configurationSegment = propertyOption?.ConfigurationNameOverride
                                       ?? attribute?.KeyOverride
                                       ?? configurationKeyAttribute?.Name
                                       ?? property.Name;

            var nextCanonicalSegments = canonicalSegments.Add(property.Name);
            var nextConfigurationSegments = configurationSegments.Add(configurationSegment);
            var propertyType = property.PropertyType;

            if (IsComplexConfigurationType(propertyType))
            {
                Traverse(
                    currentType: Nullable.GetUnderlyingType(propertyType) ?? propertyType,
                    propertyChain: nextPropertyChain,
                    canonicalSegments: nextCanonicalSegments,
                    configurationSegments: nextConfigurationSegments,
                    parameters,
                    optionLookup,
                    options);
                continue;
            }

            var allowedValues = ResolveAllowedValues(propertyType, attribute, propertyOption, options);
            var cliName = NormalizeLongCliName(propertyOption?.CliName ?? attribute?.CliKey)
                          ?? CreateDefaultCliName(nextConfigurationSegments);
            var cliShortName = NormalizeShortCliName(propertyOption?.CliShortName ?? attribute?.CliShortKey);

            parameters.Add(new(
                PropertyName: property.Name,
                Path: path,
                CanonicalKey: string.Join(":", nextCanonicalSegments),
                ConfigurationKey: string.Join(":", nextConfigurationSegments),
                CliName: cliName,
                CliShortName: cliShortName,
                Description: propertyOption?.Description ?? attribute?.Description,
                AllowedValues: allowedValues,
                Required: propertyOption?.Required ?? attribute?.Required ?? false,
                TimeUnit: propertyOption?.TimeUnit ?? attribute?.GetTimeUnitOrNull(),
                ParameterType: propertyType
                )
            );
        }
    }

    static ImmutableArray<string> ResolveAllowedValues<T>(Type propertyType, ConfigurationParameterAttribute? attribute, ConfigurationParameterOption? option, ConfigurationParameterOptions<T>? options)
    {
        if (option?.AllowedValues is { Length: > 0 } optionAllowedValues)
            return [.. optionAllowedValues];

        if (attribute?.AllowedValues is { Length: > 0 } attributeAllowedValues)
            return [.. attributeAllowedValues];

        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (!targetType.IsEnum)
            return [];

        if (options?.EnumMappings.TryGetValue(targetType, out var mappings) == true)
            return [.. mappings.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)];

        return [.. Enum.GetNames(targetType)];
    }

    static IEnumerable<PropertyInfo> GetBindableProperties(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.GetMethod is not null
                           && property.GetSetMethod(nonPublic: true) is not null
                           && property.GetIndexParameters().Length == 0)
        .OrderBy(property => property.MetadataToken);

    static bool IsComplexConfigurationType(Type type)
    {
        var targetType = Nullable.GetUnderlyingType(type) ?? type;
        if (targetType == typeof(string))
            return false;

        if (targetType.IsPrimitive || targetType.IsEnum)
            return false;

        if (targetType == typeof(decimal)
            || targetType == typeof(TimeSpan)
            || targetType == typeof(Guid)
            || targetType == typeof(Uri)
            || targetType == typeof(DateTime)
            || targetType == typeof(DateTimeOffset)
            || targetType == typeof(DateOnly)
            || targetType == typeof(TimeOnly))
            return false;

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(targetType))
            return false;

        return targetType.IsClass || (targetType.IsValueType && !targetType.IsPrimitive);
    }

    static bool TryConvertValue<T>(string rawValue, ConfigurationParameterMetadata parameter, ConfigurationParameterOptions<T>? options, out object? value, out string? error)
    {
        var targetType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

        if (targetType == typeof(string))
        {
            value = rawValue;
            error = null;
            return true;
        }

        if (targetType.IsEnum)
        {
            if (TryConvertEnum(rawValue, targetType, options, out value))
            {
                error = null;
                return true;
            }

            error = $"Configuration value '{parameter.ConfigurationKey}' for '{parameter.Path}' could not be parsed as '{targetType.Name}'.";
            return false;
        }

        if (targetType == typeof(TimeSpan))
        {
            if (TryConvertTimeSpan(rawValue, parameter.TimeUnit, out value))
            {
                error = null;
                return true;
            }

            error = $"Configuration value '{parameter.ConfigurationKey}' for '{parameter.Path}' could not be parsed as a TimeSpan.";
            return false;
        }

        if (targetType == typeof(Uri))
        {
            if (Uri.TryCreate(rawValue, UriKind.RelativeOrAbsolute, out var uri))
            {
                value = uri;
                error = null;
                return true;
            }

            value = null;
            error = $"Configuration value '{parameter.ConfigurationKey}' for '{parameter.Path}' could not be parsed as a Uri.";
            return false;
        }

        if (TryConvertUsingTypeConverter(rawValue, targetType, out value) || TryConvertUsingTryParse(rawValue, targetType, out value))
        {
            error = null;
            return true;
        }

        value = null;
        error = $"Configuration value '{parameter.ConfigurationKey}' for '{parameter.Path}' could not be parsed as '{targetType.Name}'.";
        return false;
    }

    static bool TryConvertEnum<T>(
        string rawValue,
        Type enumType,
        ConfigurationParameterOptions<T>? options,
        out object? value)
    {
        if (options?.EnumMappings.TryGetValue(enumType, out var mappings) == true
            && mappings.TryGetValue(rawValue, out value))
            return true;

        foreach (var enumName in Enum.GetNames(enumType))
        {
            if (!string.Equals(enumName, rawValue, StringComparison.OrdinalIgnoreCase))
                continue;

            value = Enum.Parse(enumType, enumName, ignoreCase: true);
            return true;
        }

        value = null;
        return false;
    }

    static bool TryConvertTimeSpan(string rawValue, ConfigurationTimeUnit? timeUnit, out object? value)
    {
        if (timeUnit is not null)
        {
            if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var units))
            {
                value = null;
                return false;
            }

            value = timeUnit switch
            {
                ConfigurationTimeUnit.Milliseconds => TimeSpan.FromMilliseconds(units),
                ConfigurationTimeUnit.Seconds => TimeSpan.FromSeconds(units),
                ConfigurationTimeUnit.Minutes => TimeSpan.FromMinutes(units),
                ConfigurationTimeUnit.Hours => TimeSpan.FromHours(units),
                _ => throw new ArgumentOutOfRangeException(nameof(timeUnit), timeUnit, null)
            };
            return true;
        }

        if (TimeSpan.TryParse(rawValue, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    static bool TryConvertUsingTypeConverter(string rawValue, Type targetType, out object? value)
    {
        var converter = TypeDescriptor.GetConverter(targetType);
        if (converter.CanConvertFrom(typeof(string)))
        {
            try
            {
                value = converter.ConvertFromInvariantString(rawValue);
                return value is not null;
            }
            catch
            {
                // ignore
            }
        }

        value = null;
        return false;
    }

    static bool TryConvertUsingTryParse(string rawValue, Type targetType, out object? value)
    {
        foreach (var method in targetType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!string.Equals(method.Name, "TryParse", StringComparison.Ordinal))
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length == 2
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == targetType.MakeByRefType())
            {
                object?[] args = [rawValue, null];
                var parsed = method.Invoke(null, args);
                if (parsed is true)
                {
                    value = args[1];
                    return true;
                }
            }

            if (parameters.Length == 3
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == typeof(IFormatProvider)
                && parameters[2].ParameterType == targetType.MakeByRefType())
            {
                object?[] args = [rawValue, CultureInfo.InvariantCulture, null];
                var parsed = method.Invoke(null, args);
                if (parsed is true)
                {
                    value = args[2];
                    return true;
                }
            }

            if (parameters.Length == 4
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == typeof(IFormatProvider)
                && parameters[2].ParameterType == typeof(DateTimeStyles)
                && parameters[3].ParameterType == targetType.MakeByRefType())
            {
                object?[] args = [rawValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, null];
                var parsed = method.Invoke(null, args);
                if (parsed is true)
                {
                    value = args[3];
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    static bool TryReadStringCollectionValues(
        IConfiguration configuration,
        ConfigurationParameterMetadata parameter,
        out ImmutableArray<string> values)
    {
        if (!IsStringCollectionType(parameter.ParameterType))
        {
            values = [];
            return false;
        }

        var section = configuration.GetSection(parameter.ConfigurationKey);
        var children = section.GetChildren().ToArray();
        if (children.Length > 0)
        {
            values = [.. OrderCollectionChildren(children)
                .Select(static child => child.Value)
                .WhereNotNullOrWhiteSpace()
                .Select(static value => value.Trim())];
            return true;
        }

        values = ParseCommaSeparatedValues(section.Value);
        return true;
    }

    static IEnumerable<IConfigurationSection> OrderCollectionChildren(IEnumerable<IConfigurationSection> children) =>
        children.OrderBy(
            static child => int.TryParse(child.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? index
                : int.MaxValue)
            .ThenBy(static child => child.Key, StringComparer.Ordinal);

    static bool IsStringCollectionType(Type type)
    {
        var targetType = Nullable.GetUnderlyingType(type) ?? type;
        if (targetType == typeof(string))
            return false;

        if (targetType.IsArray)
            return targetType.GetElementType() == typeof(string);

        return targetType.GetInterfaces()
            .Append(targetType)
            .Any(static candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && candidate.GetGenericArguments()[0] == typeof(string));
    }

    static ImmutableArray<string> ParseCommaSeparatedValues(string? rawValue) =>
        string.IsNullOrWhiteSpace(rawValue)
            ? []
            : [.. rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .WhereNotNullOrWhiteSpace()];

    static string FormatForBinder(object value, Type parameterType)
    {
        var targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        return value switch
        {
            null => string.Empty,
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
            Uri uri => uri.OriginalString,
            Enum => value.ToString()!,
            IFormattable formattable when targetType != typeof(string) => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    static FieldPath CreateFieldPath(ImmutableArray<PropertyInfo> propertyChain) =>
        new([.. propertyChain.Select(property => FieldPathSegment.ForField(property.Name))]);

    static string CreateDefaultCliName(ImmutableArray<string> configurationSegments) =>
        NormalizeLongCliName(string.Join("-", configurationSegments.Select(ToKebabCase)))!;

    static string? NormalizeLongCliName(string? cliName)
    {
        if (string.IsNullOrWhiteSpace(cliName))
            return null;

        var normalized = cliName.Trim();
        if (!normalized.StartsWith("--", StringComparison.Ordinal))
            normalized = $"--{normalized.TrimStart('-')}";

        return normalized;
    }

    static string? NormalizeShortCliName(string? cliShortName)
    {
        if (string.IsNullOrWhiteSpace(cliShortName))
            return null;

        var normalized = cliShortName.Trim();
        if (!normalized.StartsWith("-", StringComparison.Ordinal))
            normalized = $"-{normalized.TrimStart('-')}";

        if (normalized.StartsWith("--", StringComparison.Ordinal))
            normalized = $"-{normalized.TrimStart('-')}";

        return normalized;
    }

    static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        List<char> buffer = new(value.Length * 2);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current is ':' or '_' or ' ')
            {
                if (buffer.Count > 0 && buffer[^1] != '-')
                    buffer.Add('-');
                continue;
            }
            
            if (char.IsUpper(current))
            {
                var previousIsLowerOrDigit = i > 0 && (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1]));
                var nextIsLower = i + 1 < value.Length && char.IsLower(value[i + 1]);
                if (buffer.Count > 0 && buffer[^1] != '-' && (previousIsLowerOrDigit || nextIsLower))
                    buffer.Add('-');
            }

            buffer.Add(char.ToLowerInvariant(current));
        }

        return new string([.. buffer]).Trim('-');
    }

    sealed record ConfigurationParameterMetadata(
        string PropertyName,
        FieldPath Path,
        string CanonicalKey,
        string ConfigurationKey,
        string CliName,
        string? CliShortName,
        string? Description,
        ImmutableArray<string> AllowedValues,
        bool Required,
        ConfigurationTimeUnit? TimeUnit,
        Type ParameterType
        );
}
