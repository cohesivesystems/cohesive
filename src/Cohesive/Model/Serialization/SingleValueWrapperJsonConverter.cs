using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Serializes one-property value-wrapper structs as their underlying value and accepts either flat or legacy nested-object input.
/// </summary>
public sealed class SingleValueWrapperJsonConverter : JsonConverterFactory
{
    static readonly ConcurrentDictionary<Type, WrapperMetadata?> WrapperMetadataCache = new();
    static readonly ConcurrentDictionary<Type, JsonConverter> ConverterCache = new();

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        TryGetWrapperMetadata(typeToConvert, out _);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return ConverterCache.GetOrAdd(typeToConvert, static type =>
        {
            if (!TryGetWrapperMetadata(type, out var metadata))
                throw new InvalidOperationException($"Type '{type}' is not a supported single-value wrapper.");

            var converterType = typeof(SpecialSingleValueWrapperJsonConverter<,>).MakeGenericType(type, metadata.ValueType);
            return (JsonConverter)(Activator.CreateInstance(
                converterType,
                metadata.ValueProperty,
                metadata.Constructor,
                metadata.AcceptedNestedPropertyNames,
                metadata.AcceptedNestedPropertyNamesUtf8
                ) ?? throw new InvalidOperationException($"Failed to create a single-value wrapper converter for '{type}'."));
        });
    }

    static bool TryGetWrapperMetadata(Type typeToConvert, out WrapperMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        var cachedMetadata = WrapperMetadataCache.GetOrAdd(typeToConvert, static type =>
        {
            var valueProperty = ResolveValueProperty(type);
            if (valueProperty is null)
                return null;

            var constructor = type.GetConstructor([valueProperty.PropertyType]);
            if (constructor is null)
                return null;

            var acceptedNestedPropertyNames = GetAcceptedNestedPropertyNames(valueProperty, constructor);
            return new WrapperMetadata(
                valueProperty,
                constructor,
                valueProperty.PropertyType,
                acceptedNestedPropertyNames,
                GetAcceptedNestedPropertyNamesUtf8(acceptedNestedPropertyNames)
                );
        });

        if (cachedMetadata is null)
        {
            metadata = default;
            return false;
        }

        metadata = cachedMetadata.Value;
        return true;
    }

    static PropertyInfo? ResolveValueProperty(Type typeToConvert)
    {
        var publicInstanceProperties = typeToConvert
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToArray();

        var overridePropertyName = typeToConvert.GetCustomAttribute<SingleValueWrapperValuePropertyAttribute>()?.PropertyName;
        if (!string.IsNullOrWhiteSpace(overridePropertyName))
        {
            return publicInstanceProperties.SingleOrDefault(property => string.Equals(
                property.Name,
                overridePropertyName,
                StringComparison.Ordinal)
            );
        }

        var defaultValueProperty = publicInstanceProperties.SingleOrDefault(property => string.Equals(
            property.Name,
            "Value",
            StringComparison.Ordinal));
        if (defaultValueProperty is not null)
            return defaultValueProperty;

        return publicInstanceProperties.Length == 1
            ? publicInstanceProperties[0]
            : null;
    }

    static string[] GetAcceptedNestedPropertyNames(PropertyInfo valueProperty, ConstructorInfo constructor)
    {
        var acceptedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "value",
            valueProperty.Name
        };

        var jsonPropertyName = valueProperty.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
        if (!string.IsNullOrWhiteSpace(jsonPropertyName))
            acceptedNames.Add(jsonPropertyName);

        var constructorParameter = ResolveValueParameter(constructor, valueProperty);
        if (constructorParameter is not null)
        {
            if (!string.IsNullOrWhiteSpace(constructorParameter.Name))
                acceptedNames.Add(constructorParameter.Name);

            var constructorParameterJsonPropertyName = constructorParameter.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (!string.IsNullOrWhiteSpace(constructorParameterJsonPropertyName))
                acceptedNames.Add(constructorParameterJsonPropertyName);
        }

        return [..acceptedNames];
    }

    static ParameterInfo? ResolveValueParameter(ConstructorInfo constructor, PropertyInfo valueProperty)
    {
        var parameters = constructor.GetParameters();
        if (parameters.Length == 1)
            return parameters[0];

        return parameters.SingleOrDefault(parameter =>
            string.Equals(parameter.Name, valueProperty.Name, StringComparison.OrdinalIgnoreCase)
            && parameter.ParameterType == valueProperty.PropertyType);
    }

    static byte[][] GetAcceptedNestedPropertyNamesUtf8(string[] acceptedNestedPropertyNames) =>
        acceptedNestedPropertyNames
            .Select(Encoding.UTF8.GetBytes)
            .ToArray();

    readonly record struct WrapperMetadata(
        PropertyInfo ValueProperty,
        ConstructorInfo Constructor,
        Type ValueType,
        string[] AcceptedNestedPropertyNames,
        byte[][] AcceptedNestedPropertyNamesUtf8
        );

    sealed class SpecialSingleValueWrapperJsonConverter<TWrapper, TValue> : JsonConverter<TWrapper>
    {
        readonly Func<TWrapper, TValue> getValue;
        readonly Func<TValue, TWrapper> createWrapper;
        readonly string[] acceptedNestedPropertyNames;
        readonly byte[][] acceptedNestedPropertyNamesUtf8;
        readonly string expectedPropertyNames;

        public SpecialSingleValueWrapperJsonConverter(
            PropertyInfo valueProperty,
            ConstructorInfo constructor,
            string[] acceptedNestedPropertyNames,
            byte[][] acceptedNestedPropertyNamesUtf8
            )
        {
            ArgumentNullException.ThrowIfNull(valueProperty);
            ArgumentNullException.ThrowIfNull(constructor);
            ArgumentNullException.ThrowIfNull(acceptedNestedPropertyNames);
            ArgumentNullException.ThrowIfNull(acceptedNestedPropertyNamesUtf8);

            this.acceptedNestedPropertyNames = acceptedNestedPropertyNames;
            this.acceptedNestedPropertyNamesUtf8 = acceptedNestedPropertyNamesUtf8;
            expectedPropertyNames = string.Join(", ", acceptedNestedPropertyNames.Order(StringComparer.OrdinalIgnoreCase));

            var wrapperParameter = Expression.Parameter(typeof(TWrapper), "wrapper");
            getValue = Expression.Lambda<Func<TWrapper, TValue>>(
                body: Expression.Property(wrapperParameter, valueProperty),
                parameters: wrapperParameter
                ).Compile();

            var valueParameter = Expression.Parameter(typeof(TValue), "value");
            createWrapper = Expression.Lambda<Func<TValue, TWrapper>>(
                body: Expression.New(constructor, valueParameter),
                parameters: valueParameter
                ).Compile();
        }

        public override TWrapper Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                return ReadNestedValueObject(ref reader, typeToConvert, options);

            var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
            return createWrapper(value!);
        }

        public override void Write(Utf8JsonWriter writer, TWrapper value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(options);

            JsonSerializer.Serialize(writer, getValue(value), options);
        }

        TWrapper ReadNestedValueObject(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var foundValue = false;
            TValue nestedValue = default!;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    if (!foundValue)
                        throw new JsonException($"Expected '{typeToConvert.Name}' to contain one of: {expectedPropertyNames}.");

                    return createWrapper(nestedValue);
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException($"Expected a property name while reading '{typeToConvert.Name}'.");

                var isAcceptedPropertyName = IsAcceptedNestedPropertyName(ref reader, options);
                if (!reader.Read())
                    throw new JsonException($"Unexpected end of JSON while reading '{typeToConvert.Name}'.");

                if (isAcceptedPropertyName)
                {
                    if (!foundValue)
                    {
                        nestedValue = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
                        foundValue = true;
                    }
                    else
                    {
                        reader.Skip();
                    }

                    continue;
                }

                reader.Skip();
            }

            throw new JsonException($"Unexpected end of JSON while reading '{typeToConvert.Name}'.");
        }

        bool IsAcceptedNestedPropertyName(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            foreach (var acceptedPropertyName in acceptedNestedPropertyNamesUtf8)
            {
                if (reader.ValueTextEquals(acceptedPropertyName))
                    return true;
            }

            var propertyName = reader.GetString();
            if (string.IsNullOrEmpty(propertyName))
                return false;

            var comparer = options.PropertyNameCaseInsensitive
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            foreach (var acceptedPropertyName in acceptedNestedPropertyNames)
            {
                if (comparer.Equals(propertyName, acceptedPropertyName))
                    return true;
            }

            return false;
        }
    }
}

/// <summary>
/// Overrides which public property should be treated as the wrapped scalar value for single-value wrapper JSON conversion.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class SingleValueWrapperValuePropertyAttribute : Attribute
{
    /// <summary>
    /// Creates a value-property override.
    /// </summary>
    public SingleValueWrapperValuePropertyAttribute(string propertyName)
    {
        PropertyName = Guard.RequireNotNullOrWhiteSpace(propertyName);
    }

    /// <summary>
    /// Name of the public property that holds the wrapped scalar value.
    /// </summary>
    public string PropertyName { get; }
}
