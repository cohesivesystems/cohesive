using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using DurableTask.Core.Serializing;

namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Durable Task <see cref="DataConverter"/> backed by <see cref="System.Text.Json"/>.
/// </summary>
public sealed class DurableTaskSystemTextJsonDataConverter : JsonDataConverter
{
    internal const string TypedValueTypePropertyName = "$type";
    internal const string TypedValuePropertyName = "$value";

    readonly JsonSerializerOptions compactOptions;
    readonly JsonSerializerOptions formattedOptions;

    /// <summary>
    /// Creates a new converter backed by <see cref="System.Text.Json"/>.
    /// </summary>
    /// <param name="options">Optional serializer options to clone before adding the Cohesive Durable Task converters.</param>
    public DurableTaskSystemTextJsonDataConverter(JsonSerializerOptions? options = null)
    {
        compactOptions = CreateJsonOptions(options);
        formattedOptions = new(compactOptions)
        {
            WriteIndented = true
        };
    }

    /// <summary>
    /// Creates serializer options configured for Durable Task payloads.
    /// </summary>
    /// <param name="options">Optional serializer options to clone; the supplied instance is never mutated.</param>
    /// <returns>A mutable options instance containing the required Cohesive Durable Task converters.</returns>
    public static JsonSerializerOptions CreateJsonOptions(JsonSerializerOptions? options = null)
    {
        var resolved = options is null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
        AddConverterIfMissing<StructuredQuantityJsonConverterFactory>(resolved, static () => new StructuredQuantityJsonConverterFactory());
        AddConverterIfMissing<DurableTaskObjectJsonConverter>(resolved, static () => new DurableTaskObjectJsonConverter());
        AddConverterIfMissing<EntitySnapshotJsonConverterFactory>(resolved, static () => new EntitySnapshotJsonConverterFactory());
        return resolved;
    }

    /// <inheritdoc />
    public override string Serialize(object value)
    {
        return Serialize(value, formatted: false);
    }

    /// <inheritdoc />
    public override string Serialize(object value, bool formatted)
    {
        if (value is null)
            return "null";

        var options = formatted ? formattedOptions : compactOptions;
        return JsonSerializer.Serialize(value, value.GetType(), options);
    }

    /// <inheritdoc />
    public override object? Deserialize(string data, Type objectType)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        if (string.IsNullOrWhiteSpace(data))
            return null;

        if (objectType != typeof(object) && TryExtractTypedValuePayload(data, out var payloadJson))
            return JsonSerializer.Deserialize(payloadJson, objectType, compactOptions);

        return JsonSerializer.Deserialize(data, objectType, compactOptions);
    }

    internal static bool TryExtractTypedValuePayload(string data, out string payloadJson)
    {
        payloadJson = string.Empty;

        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
            return false;

        if (!document.RootElement.TryGetProperty(TypedValueTypePropertyName, out _))
            return false;

        if (!document.RootElement.TryGetProperty(TypedValuePropertyName, out var valueProperty))
            return false;

        payloadJson = valueProperty.GetRawText();
        return true;
    }

    static void AddConverterIfMissing<TConverter>(JsonSerializerOptions options, Func<TConverter> factory)
        where TConverter : JsonConverter
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);

        foreach (var converter in options.Converters)
        {
            if (converter is TConverter)
                return;
        }

        options.Converters.Add(factory());
    }

    sealed class DurableTaskObjectJsonConverter : JsonConverter<object>
    {
        const string ValuesPropertyName = "$values";

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.Null)
                return null;

            using var document = JsonDocument.ParseValue(ref reader);
            return DeserializeObject(document.RootElement, options);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            var (normalizedValue, normalizedType) = NormalizePayload(value);
            writer.WriteStartObject();
            writer.WriteString(TypedValueTypePropertyName, GetTypeIdentifier(normalizedType));
            writer.WritePropertyName(TypedValuePropertyName);

            if (normalizedType == typeof(object))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                JsonSerializer.Serialize(writer, normalizedValue, normalizedType, options);
            }

            writer.WriteEndObject();
        }

        static (object Value, Type Type) NormalizePayload(object value)
        {
            ArgumentNullException.ThrowIfNull(value);

            var runtimeType = value.GetType();
            if (!ShouldNormalizeCollectionWrapper(runtimeType))
                return (value, runtimeType);

            if (TryNormalizeDictionary(value, runtimeType, out var dictionaryValue, out var dictionaryType))
                return (dictionaryValue, dictionaryType);

            if (TryNormalizeList(value, runtimeType, out var listValue, out var listType))
                return (listValue, listType);

            return (value, runtimeType);
        }

        static bool ShouldNormalizeCollectionWrapper(Type runtimeType) =>
            Attribute.IsDefined(runtimeType, typeof(CompilerGeneratedAttribute), inherit: false)
            || runtimeType.FullName?.StartsWith("<>", StringComparison.Ordinal) == true;

        static bool TryNormalizeDictionary(object value, Type runtimeType, out object normalizedValue, out Type normalizedType)
        {
            foreach (var candidate in runtimeType.GetInterfaces())
            {
                if (!candidate.IsGenericType)
                    continue;

                var genericType = candidate.GetGenericTypeDefinition();
                if (genericType != typeof(IReadOnlyDictionary<,>) && genericType != typeof(IDictionary<,>))
                    continue;

                var typeArguments = candidate.GetGenericArguments();
                normalizedType = typeof(Dictionary<,>).MakeGenericType(typeArguments);
                normalizedValue = Activator.CreateInstance(normalizedType, value)
                    ?? throw new JsonException($"Unable to materialize durable dictionary payload '{runtimeType.FullName}'.");
                return true;
            }

            normalizedValue = null!;
            normalizedType = null!;
            return false;
        }

        static bool TryNormalizeList(object value, Type runtimeType, out object normalizedValue, out Type normalizedType)
        {
            foreach (var candidate in runtimeType.GetInterfaces())
            {
                if (!candidate.IsGenericType)
                    continue;

                var genericType = candidate.GetGenericTypeDefinition();
                if (genericType != typeof(IReadOnlyList<>) && genericType != typeof(IList<>))
                    continue;

                var elementType = candidate.GetGenericArguments()[0];
                normalizedType = elementType.MakeArrayType();
                normalizedValue = ToArrayMethod
                    .MakeGenericMethod(elementType)
                    .Invoke(null, [value])
                    ?? throw new JsonException($"Unable to materialize durable list payload '{runtimeType.FullName}'.");
                return true;
            }

            normalizedValue = null!;
            normalizedType = null!;
            return false;
        }

        static readonly MethodInfo ToArrayMethod = typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Enumerable.ToArray) && method.GetParameters().Length == 1);

        static object? DeserializeObject(JsonElement element, JsonSerializerOptions options)
        {
            if (TryGetTypedPayload(element, out var payloadType, out var payloadJson))
                return JsonSerializer.Deserialize(payloadJson, payloadType, options);

            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.False => false,
                JsonValueKind.True => true,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt32(out var int32) => int32,
                JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
                JsonValueKind.Number when element.TryGetDecimal(out var dec) => dec,
                JsonValueKind.Number => element.GetDouble(),
                JsonValueKind.Object => ReadObject(element, options),
                JsonValueKind.Array => ReadArray(element, options),
                _ => throw new JsonException($"Unsupported JSON token '{element.ValueKind}' for Durable Task object payload.")
            };
        }

        static Dictionary<string, object?> ReadObject(JsonElement element, JsonSerializerOptions options)
        {
            Dictionary<string, object?> values = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                values[property.Name] = DeserializeObject(property.Value, options);
            return values;
        }

        static object?[] ReadArray(JsonElement element, JsonSerializerOptions options)
        {
            var values = new object?[element.GetArrayLength()];
            var index = 0;
            foreach (var item in element.EnumerateArray())
                values[index++] = DeserializeObject(item, options);
            return values;
        }

        static bool TryGetTypedPayload(JsonElement element, out Type payloadType, out string payloadJson)
        {
            payloadType = null!;
            payloadJson = string.Empty;

            if (element.ValueKind is not JsonValueKind.Object)
                return false;

            if (!element.TryGetProperty(TypedValueTypePropertyName, out var typeProperty)
                || typeProperty.ValueKind is not JsonValueKind.String)
                return false;

            payloadType = ResolvePayloadType(typeProperty.GetString()!);

            if (element.TryGetProperty(TypedValuePropertyName, out var valueProperty))
            {
                payloadJson = valueProperty.GetRawText();
                return true;
            }

            if (element.TryGetProperty(ValuesPropertyName, out var valuesProperty))
            {
                payloadJson = valuesProperty.GetRawText();
                return true;
            }

            payloadJson = StripTypeProperty(element);
            return true;
        }

        static string StripTypeProperty(JsonElement element)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, TypedValueTypePropertyName, StringComparison.Ordinal))
                        continue;

                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        static string GetTypeIdentifier(Type type)
        {
            return type.AssemblyQualifiedName
                ?? $"{type.FullName}, {type.Assembly.GetName().Name}";
        }

        static Type ResolvePayloadType(string typeName)
        {
            var resolved = Type.GetType(typeName, ResolveAssembly, null, throwOnError: false);
            if (resolved is not null)
                return resolved;

            throw new JsonException($"Unable to resolve Durable Task payload type '{typeName}'.");
        }

        static Assembly? ResolveAssembly(AssemblyName assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var candidate = assembly.GetName();
                if (AssemblyName.ReferenceMatchesDefinition(candidate, assemblyName))
                    return assembly;

                if (string.Equals(candidate.Name, assemblyName.Name, StringComparison.Ordinal))
                    return assembly;
            }

            return null;
        }
    }

    sealed class EntitySnapshotJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(EntitySnapshot<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var entityType = typeToConvert.GetGenericArguments()[0];
            var converterType = typeof(EntitySnapshotJsonConverter<>).MakeGenericType(entityType);
            return (JsonConverter)(Activator.CreateInstance(converterType)
                ?? throw new JsonException($"Unable to create durable converter for '{typeToConvert.FullName}'."));
        }
    }

    sealed class EntitySnapshotJsonConverter<TEntity> : JsonConverter<EntitySnapshot<TEntity>>
        where TEntity : Entity
    {
        public override EntitySnapshot<TEntity>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (!document.RootElement.TryGetProperty("State", out var stateProperty))
                throw new JsonException($"Durable entity snapshot payload for '{typeof(TEntity).FullName}' is missing required property 'State'.");

            var state = JsonSerializer.Deserialize<EntityState>(stateProperty.GetRawText(), options)
                ?? throw new JsonException($"Durable entity snapshot payload for '{typeof(TEntity).FullName}' did not deserialize an '{nameof(EntityState)}'.");

            return new(ResolveEntityInstance(), state);
        }

        public override void Write(Utf8JsonWriter writer, EntitySnapshot<TEntity> value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(value);

            writer.WriteStartObject();
            writer.WritePropertyName("State");
            JsonSerializer.Serialize(writer, value.State, options);
            writer.WriteEndObject();
        }

        static TEntity ResolveEntityInstance()
        {
            var instanceProperty = typeof(TEntity).GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static);
            if (instanceProperty?.GetValue(null) is TEntity entity)
                return entity;

            if (Activator.CreateInstance(typeof(TEntity)) is TEntity created)
                return created;

            throw new JsonException(
                $"Unable to resolve entity definition instance for durable snapshot type '{typeof(TEntity).FullName}'. " +
                "Expected a public static Instance property or parameterless constructor.");
        }
    }
}
