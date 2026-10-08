using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

internal enum SerializedEnumMemberCatalogFailure
{
    None,
    UnsupportedConverter,
    AmbiguousWireMember
}

internal sealed class SerializedEnumMemberCatalog
{
    static readonly ConditionalWeakTable<Type, CachedDiscovery> Discoveries = new();
    readonly IReadOnlyDictionary<string, string> clrToWire;
    readonly IReadOnlyDictionary<string, string> wireToClr;

    SerializedEnumMemberCatalog(
        IReadOnlyDictionary<string, string> clrToWire,
        IReadOnlyDictionary<string, string> wireToClr)
    {
        this.clrToWire = clrToWire;
        this.wireToClr = wireToClr;
        WireMembers = [.. clrToWire.Values];
    }

    public ImmutableArray<string> WireMembers { get; }

    public bool TryGetClrName(string wireName, out string clrName) =>
        TryTranslate(wireToClr, wireName, out clrName);

    public bool TryGetWireName(string clrName, out string wireName) =>
        TryTranslate(clrToWire, clrName, out wireName);

    public static bool TryCreate(
        Type enumType,
        out SerializedEnumMemberCatalog? catalog,
        out SerializedEnumMemberCatalogFailure failure,
        out Type? unsupportedConverter,
        bool useClrNamesForUnsupportedConverter = false)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        if (!enumType.IsEnum)
        {
            throw new ArgumentException($"Type '{enumType}' is not an enum.", nameof(enumType));
        }

        var prepared = Discoveries.GetValue(enumType, static type => new(type)).Get(useClrNamesForUnsupportedConverter);
        catalog = prepared.Catalog;
        failure = prepared.Failure;
        unsupportedConverter = prepared.Converter;
        return catalog is not null;
    }

    // Enum declarations are stable metadata. Weak keys do not add permanent CLR type retention;
    // selected lazy entries coordinate first preparation without sharing mutable discovery state.
    sealed class CachedDiscovery
    {
        readonly Lazy<Discovery> strict;
        readonly Lazy<Discovery> fallback;

        internal CachedDiscovery(Type type)
        {
            strict = new(() => Discover(type));
            fallback = new(() => strict.Value.Failure == SerializedEnumMemberCatalogFailure.UnsupportedConverter
                ? DiscoverMembers(type, false) : strict.Value);
        }

        internal Discovery Get(bool useClrNames) => (useClrNames ? fallback : strict).Value;
    }

    readonly record struct Discovery(SerializedEnumMemberCatalog? Catalog,
        SerializedEnumMemberCatalogFailure Failure, Type? Converter);

    static Discovery Discover(Type enumType)
    {
        var converterAttribute = enumType.GetCustomAttribute<JsonConverterAttribute>(inherit: true);
        var converter = converterAttribute?.ConverterType;
        var useJsonMemberNames = converterAttribute is not null
                                 && converter is not null
                                 && IsStandardStringEnumConverter(converter);
        if (converterAttribute is not null && !useJsonMemberNames)
        {
            return new(null, SerializedEnumMemberCatalogFailure.UnsupportedConverter, converter);
        }

        return DiscoverMembers(enumType, useJsonMemberNames);
    }

    static Discovery DiscoverMembers(Type enumType, bool useJsonMemberNames)
    {
        Dictionary<string, string> clrToWire = new(StringComparer.Ordinal);
        Dictionary<string, string> wireToClr = new(StringComparer.Ordinal);
        foreach (var clrName in Enum.GetNames(enumType))
        {
            var wireName = useJsonMemberNames
                ? enumType.GetField(clrName, BindingFlags.Public | BindingFlags.Static)?
                      .GetCustomAttribute<JsonStringEnumMemberNameAttribute>(inherit: false)?.Name ?? clrName
                : clrName;
            if (!wireToClr.TryAdd(wireName, clrName))
            {
                return new(null, SerializedEnumMemberCatalogFailure.AmbiguousWireMember, null);
            }
            clrToWire.Add(clrName, wireName);
        }

        return new(new(clrToWire, wireToClr), SerializedEnumMemberCatalogFailure.None, null);
    }

    static bool TryTranslate(
        IReadOnlyDictionary<string, string> names,
        string source,
        out string target)
    {
        if (names.TryGetValue(source, out target!))
            return true;

        var parts = source.Split(", ", StringSplitOptions.None);
        if (parts.Length <= 1)
        {
            target = string.Empty;
            return false;
        }

        string[] translated = new string[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!names.TryGetValue(parts[index], out translated[index]!))
            {
                target = string.Empty;
                return false;
            }
        }

        target = string.Join(", ", translated);
        return true;
    }

    static bool IsStandardStringEnumConverter(Type converter) =>
        converter == typeof(JsonStringEnumConverter)
        || converter.IsGenericType
        && string.Equals(
            converter.GetGenericTypeDefinition().FullName,
            "System.Text.Json.Serialization.JsonStringEnumConverter`1",
            StringComparison.Ordinal);
}
