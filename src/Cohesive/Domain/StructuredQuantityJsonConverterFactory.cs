using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Domain;

/// <summary>
/// JSON converter factory for structured quantity wrappers.
/// </summary>
public sealed class StructuredQuantityJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => TryGetStructuredQuantityInterface(typeToConvert, out _);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (!TryGetStructuredQuantityInterface(typeToConvert, out var structuredQuantityInterface))
        {
            throw new InvalidOperationException(
                $"Type '{typeToConvert}' does not implement IStructuredQuantity<TSelf,TDimension,TRep>.");
        }

        var genericArguments = structuredQuantityInterface.GetGenericArguments();
        var converterType = typeof(StructuredQuantityJsonConverter<,,>).MakeGenericType(
            typeToConvert,
            genericArguments[1],
            genericArguments[2]);

        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    static bool TryGetStructuredQuantityInterface(Type type, out Type structuredQuantityInterface)
    {
        structuredQuantityInterface = type.GetInterfaces()
            .FirstOrDefault(x =>
                x.IsGenericType
                && x.GetGenericTypeDefinition() == typeof(IStructuredQuantity<,,>)
                && x.GetGenericArguments()[0] == type)!;

        return structuredQuantityInterface is not null;
    }

    sealed class StructuredQuantityJsonConverter<TQuantity, TDimension, TRep> : JsonConverter<TQuantity>
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
    {
        public override TQuantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                throw new JsonException($"Cannot deserialize null into '{typeof(TQuantity).Name}'.");

            TRep baseValue;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                if (!TryExtractBaseValueElement(document.RootElement, out var baseValueElement))
                {
                    throw new JsonException(
                        $"Quantity '{typeof(TQuantity).Name}' JSON payload must contain 'baseValue' or 'value.baseValue'.");
                }

                baseValue = DeserializeBaseValue(baseValueElement, options);
            }
            else
            {
                baseValue = DeserializeBaseValue(ref reader, options);
            }

            return TQuantity.FromValue(Quantity<TDimension, TRep>.FromBase(baseValue));
        }

        public override void Write(Utf8JsonWriter writer, TQuantity value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value.Value.BaseValue, options);
        }

        static bool TryExtractBaseValueElement(JsonElement root, out JsonElement baseValueElement)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                baseValueElement = default;
                return false;
            }

            if (TryGetPropertyIgnoreCase(root, "baseValue", out baseValueElement))
                return true;

            if (TryGetPropertyIgnoreCase(root, "value", out var valueElement)
                && valueElement.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(valueElement, "baseValue", out baseValueElement))
            {
                return true;
            }

            return false;
        }

        static bool TryGetPropertyIgnoreCase(JsonElement objectElement, string propertyName, out JsonElement value)
        {
            foreach (var property in objectElement.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        static TRep DeserializeBaseValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            var deserialized = JsonSerializer.Deserialize<TRep>(ref reader, options);
            return deserialized is null
                ? throw new JsonException($"Quantity '{typeof(TQuantity).Name}' base value deserialized to null.")
                : deserialized;
        }

        static TRep DeserializeBaseValue(JsonElement element, JsonSerializerOptions options)
        {
            var deserialized = element.Deserialize<TRep>(options);
            return deserialized is null
                ? throw new JsonException($"Quantity '{typeof(TQuantity).Name}' base value deserialized to null.")
                : deserialized;
        }
    }
}
