using System.Reflection;
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
    readonly IReadOnlyDictionary<string, string> clrToWire;
    readonly IReadOnlyDictionary<string, string> wireToClr;

    SerializedEnumMemberCatalog(
        IReadOnlyDictionary<string, string> clrToWire,
        IReadOnlyDictionary<string, string> wireToClr)
    {
        this.clrToWire = clrToWire;
        this.wireToClr = wireToClr;
    }

    public IReadOnlyList<string> WireMembers => [.. clrToWire.Values];

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

        var converterAttribute = enumType.GetCustomAttribute<JsonConverterAttribute>(inherit: true);
        var converter = converterAttribute?.ConverterType;
        var useJsonMemberNames = converterAttribute is not null
                                 && converter is not null
                                 && IsStandardStringEnumConverter(converter);
        if (converterAttribute is not null
            && !useJsonMemberNames
            && !useClrNamesForUnsupportedConverter)
        {
            catalog = null;
            failure = SerializedEnumMemberCatalogFailure.UnsupportedConverter;
            unsupportedConverter = converter;
            return false;
        }

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
                catalog = null;
                failure = SerializedEnumMemberCatalogFailure.AmbiguousWireMember;
                unsupportedConverter = null;
                return false;
            }
            clrToWire.Add(clrName, wireName);
        }

        catalog = new(clrToWire, wireToClr);
        failure = SerializedEnumMemberCatalogFailure.None;
        unsupportedConverter = null;
        return true;
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
