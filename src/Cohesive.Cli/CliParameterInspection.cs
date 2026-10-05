using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Cli;

// Reflection belongs to typed authoring, not the declaration model. Cache complete property chains.
static class CliParameterInspection
{
    static readonly ConcurrentDictionary<(Type, FieldPath), PropertyInfo[]> chains = new();

    internal static IReadOnlyDictionary<string, object?> ReadValues<T>(T configuration,
        IReadOnlyList<ConfigurationParameterDescriptor> descriptors)
    {
        Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            if (configuration is CliValues explicitValues)
            {
                values.Add(descriptor.ConfigurationKey, explicitValues.ReadValue(descriptor.ConfigurationKey));
                continue;
            }
            var chain = chains.GetOrAdd((typeof(T), descriptor.Path), static key =>
            {
                var type = key.Item1;
                var properties = new PropertyInfo[key.Item2.Segments.Length];
                for (var i = 0; i < properties.Length; i++)
                {
                    var property = type.GetProperty(key.Item2.Segments[i].Segment!)
                        ?? throw new InvalidOperationException($"Parameter path '{key.Item2}' is not readable on '{key.Item1}'.");
                    properties[i] = property;
                    type = property.PropertyType;
                }
                return properties;
            });
            object? value = configuration;
            foreach (var property in chain)
            {
                if (value is null) break;
                value = property.GetValue(value);
            }
            values.Add(descriptor.ConfigurationKey, value);
        }
        return values;
    }

    internal static ImmutableArray<CliParameterProvenance> Explain(
        IConfigurationRoot configuration, IReadOnlyList<CliConfigurationProvider> providers,
        IReadOnlyList<ConfigurationParameterDescriptor> descriptors,
        IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<string, string>? environmentNames,
        string? environmentPrefix, bool explicitDeclarations)
    {
        var result = ImmutableArray.CreateBuilder<CliParameterProvenance>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            var origins = ImmutableArray.CreateBuilder<CliConfigurationOrigin>();
            var value = values[descriptor.ConfigurationKey];
            var keys = ConfigurationParameterParser.GetValueKeys(configuration, descriptor);
            foreach (var key in keys) origins.Add(Origin(key, descriptor));
            if (origins.Count == 0) origins.Add(FallbackOrigin(descriptor.ConfigurationKey, descriptor));
            result.Add(new(descriptor.ConfigurationKey, descriptor.CliName,
                descriptor.Sensitive ? ConfigurationParameterParser.RedactedValue : Display(value), descriptor.Sensitive, origins.ToImmutable()));
        }
        return result.MoveToImmutable();

        CliConfigurationOrigin Origin(string key, ConfigurationParameterDescriptor descriptor)
        {
            // Empty raw inputs are ignored by the shared binder, so its effective CLR fallback is the source.
            for (var i = providers.Count - 1; i >= 0; i--)
            {
                var provider = providers[i];
                if (!provider.Provider.TryGet(key, out var raw)) continue;
                if (string.IsNullOrWhiteSpace(raw)) break;
                var source = provider.Kind switch
                {
                    CliConfigurationSourceKind.CommandLine => descriptor.CliName,
                    CliConfigurationSourceKind.Environment when provider.ExplicitEnvironment =>
                        environmentNames![descriptor.ConfigurationKey],
                    CliConfigurationSourceKind.Environment => $"{provider.Name}; binding key {key}; prefix {environmentPrefix ?? "<none>"}",
                    _ => provider.Name
                };
                return new(key, provider.Kind, source);
            }
            return FallbackOrigin(key, descriptor);
        }

        CliConfigurationOrigin FallbackOrigin(string key, ConfigurationParameterDescriptor descriptor) =>
            !explicitDeclarations ? new(key, CliConfigurationSourceKind.ClrDefault, "CLR initialization or type default") :
            values[descriptor.ConfigurationKey] is null ? new(key, CliConfigurationSourceKind.Absent, "no supplied value") :
            new(key, CliConfigurationSourceKind.DeclarationDefault, "option declaration");
    }

    static string? Display(object? value) => value switch
    {
        null => null,
        string text => text,
        IEnumerable collection => JsonSerializer.Serialize(collection.Cast<object?>().Select(Display).ToArray()),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}

sealed record CliConfigurationProvider(
    IConfigurationProvider Provider, CliConfigurationSourceKind Kind, string Name, bool ExplicitEnvironment);
