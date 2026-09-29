using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>Preserves a value type's declared JSON profile inside a foreign envelope.</summary>
/// <remarks>Factories are resolved once per CLR type and frozen. This adapter preserves representation;
/// semantic document admission remains the responsibility of the owning validator. Declared profiles
/// must not themselves register this factory, which would recursively select the same value contract.</remarks>
public sealed class DeclaredJsonValueConverterFactory : JsonConverterFactory
{
    static readonly ConditionalWeakTable<Type, Lazy<JsonSerializerOptions>> Profiles = new();

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.GetCustomAttribute<JsonContractOptionsAttribute>(inherit: false) is not null;

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var profile = Profiles.GetValue(typeToConvert, static type => new(() =>
        {
            var declaration = type.GetCustomAttribute<JsonContractOptionsAttribute>(inherit: false)
                ?? throw new InvalidOperationException($"Type '{type}' has no JSON contract declaration.");
            var resolved = JsonContractOptionsAttribute.Resolve(declaration);
            if (resolved.Converters.Any(converter => converter is DeclaredJsonValueConverterFactory))
                throw new InvalidOperationException("A declared value profile cannot recursively register DeclaredJsonValueConverterFactory.");
            return resolved;
        })).Value;
        return (JsonConverter)Activator.CreateInstance(
            typeof(Converter<>).MakeGenericType(typeToConvert), profile)!;
    }

    sealed class Converter<T>(JsonSerializerOptions profile) : JsonConverter<T>
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T>(ref reader, profile);
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, profile);
    }
}
