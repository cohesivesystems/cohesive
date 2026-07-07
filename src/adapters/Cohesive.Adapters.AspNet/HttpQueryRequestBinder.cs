using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Cohesive.Adapters.AspNet;

/// <summary>
/// Binds HTTP query-string values to request DTOs using the same naming rules as Cohesive API HTTP projections.
/// </summary>
public static class HttpQueryRequestBinder
{
    static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Binds the request query string to <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">Request DTO type.</typeparam>
    /// <param name="httpContext">HTTP context containing the query string and JSON options.</param>
    /// <returns>A request DTO populated from query-string values.</returns>
    /// <exception cref="BadHttpRequestException">The query request type could not be bound.</exception>
    public static TRequest Bind<TRequest>(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return Bind<TRequest>(httpContext.Request.Query, ResolveJsonOptions(httpContext)) ?? throw new BadHttpRequestException($"Query request type '{typeof(TRequest).Name}' could not be bound.");
    }

    /// <summary>
    /// Binds the query string to <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">Request DTO type.</typeparam>
    /// <param name="query">Query values to bind.</param>
    /// <param name="jsonOptions">JSON options used to materialize the DTO.</param>
    /// <returns>A request DTO populated from query-string values.</returns>
    public static TRequest? Bind<TRequest>(IQueryCollection query, JsonSerializerOptions? jsonOptions = null) =>
        (TRequest?)Bind(query, typeof(TRequest), jsonOptions, returnNullWhenEmpty: false);

    /// <summary>
    /// Binds the request query string to a DTO type, returning <see langword="null"/> when no matching query values exist.
    /// </summary>
    /// <param name="httpContext">HTTP context containing the query string and JSON options.</param>
    /// <param name="requestType">Request DTO type.</param>
    /// <returns>A bound request DTO, or <see langword="null"/> when no matching values are present.</returns>
    public static object? BindOrNull(HttpContext httpContext, Type requestType)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(requestType);
        return Bind(httpContext.Request.Query, requestType, ResolveJsonOptions(httpContext), returnNullWhenEmpty: true);
    }

    static object? Bind(
        IQueryCollection query,
        Type requestType,
        JsonSerializerOptions? jsonOptions,
        bool returnNullWhenEmpty
        )
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(requestType);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var properties = requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            if (property.GetMethod is null || property.GetMethod.IsStatic || property.GetIndexParameters().Length != 0)
                continue;
            if (property.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true) is not null)
                continue;

            var name = ResolveQueryParameterName(property);
            if (!query.TryGetValue(name, out var rawValues))
                continue;

            values[ResolveJsonPropertyName(property)] = ConvertQueryValue(name, rawValues, property.PropertyType);
        }

        if (values.Count == 0 && returnNullWhenEmpty)
            return null;

        var resolvedJsonOptions = jsonOptions ?? DefaultJsonOptions;
        var json = JsonSerializer.SerializeToElement(values, resolvedJsonOptions);
        return json.Deserialize(requestType, resolvedJsonOptions);
    }

    static object? ConvertQueryValue(string parameterName, IReadOnlyList<string?> values, Type targetType)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var normalizedType = nullableType ?? targetType;
        var allowsNull = nullableType is not null || !targetType.IsValueType;
        if (TryGetSequenceElementType(normalizedType, out var elementType))
        {
            var normalizedValues = values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim())
                .ToArray();
            var array = Array.CreateInstance(elementType, normalizedValues.Length);
            for (var i = 0; i < normalizedValues.Length; i++)
                array.SetValue(value: ConvertScalar(parameterName, normalizedValues[i], elementType), index: i);
            return array;
        }
        var value = values.Count == 0 ? null : values[^1];
        return ConvertScalar(parameterName, value, normalizedType, allowsNull);
    }

    static object? ConvertScalar(string parameterName, string? value, Type targetType, bool allowsNull = false)
    {
        if (value is null)
            return null;
        if (allowsNull && targetType != typeof(string) && string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            if (targetType == typeof(string))
                return value;
            if (targetType == typeof(bool))
                return bool.Parse(value);
            if (targetType == typeof(int))
                return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))
                return long.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(decimal))
                return decimal.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
                return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(Guid))
                return Guid.Parse(value);
            if (targetType == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (targetType == typeof(DateTime))
                return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (targetType == typeof(DateOnly))
                return DateOnly.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(TimeOnly))
                return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
            if (targetType.IsEnum)
                return Enum.Parse(targetType, value, ignoreCase: true);
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
        {
            throw new BadHttpRequestException(
                $"Query parameter '{parameterName}' could not be converted to {targetType.Name}.",
                error
                );
        }

        return value;
    }

    static bool TryGetSequenceElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = typeof(void);
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType() ?? typeof(void);
            return elementType != typeof(void);
        }

        var interfaces = type.GetInterfaces();
        for (var i = 0; i < interfaces.Length; i++)
        {
            var candidate = interfaces[i];
            if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            elementType = candidate.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(void);
        return false;
    }

    static string ResolveQueryParameterName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? ToSnakeCase(property.Name);

    static string ResolveJsonPropertyName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? property.Name;

    static JsonSerializerOptions ResolveJsonOptions(HttpContext httpContext) =>
        httpContext.RequestServices?.GetService<IOptions<HttpJsonOptions>>()?.Value.SerializerOptions ?? DefaultJsonOptions;

    static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsUpper(current))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
