using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Domain;

/// <summary>
/// Serializes enum values using their <see cref="CodeAttribute.Code"/> value when one is present.
/// </summary>
public sealed class CodeEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    static readonly CodeEnumJsonConverterCache<TEnum> Cache = CodeEnumJsonConverterCache<TEnum>.Create();

    /// <inheritdoc />
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (value is not null && Cache.TryParse(value, out var parsed))
                return parsed;

            throw new JsonException($"Unknown {typeof(TEnum).Name} code '{value}'.");
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var number = reader.GetInt64();
            return (TEnum)Enum.ToObject(typeof(TEnum), number);
        }

        throw new JsonException($"Expected string or number token for {typeof(TEnum).Name}.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Cache.GetCode(value));
}

sealed class CodeEnumJsonConverterCache<TEnum>
    where TEnum : struct, Enum
{
    readonly IReadOnlyDictionary<TEnum, string> codesByValue;
    readonly IReadOnlyDictionary<string, TEnum> valuesByCode;

    CodeEnumJsonConverterCache(
        IReadOnlyDictionary<TEnum, string> codesByValue,
        IReadOnlyDictionary<string, TEnum> valuesByCode
        )
    {
        this.codesByValue = codesByValue;
        this.valuesByCode = valuesByCode;
    }

    public static CodeEnumJsonConverterCache<TEnum> Create()
    {
        var codesByValue = new Dictionary<TEnum, string>();
        var valuesByCode = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var code = field.GetCustomAttribute<CodeAttribute>()?.Code ?? field.Name;

            codesByValue[value] = code;
            valuesByCode[code] = value;
            valuesByCode[field.Name] = value;
        }

        return new(codesByValue, valuesByCode);
    }

    public string GetCode(TEnum value) =>
        codesByValue.TryGetValue(value, out var code)
            ? code
            : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    public bool TryParse(string code, out TEnum value)
    {
        if (valuesByCode.TryGetValue(code, out value))
            return true;

        if (long.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            value = (TEnum)Enum.ToObject(typeof(TEnum), number);
            return true;
        }

        value = default;
        return false;
    }
}
