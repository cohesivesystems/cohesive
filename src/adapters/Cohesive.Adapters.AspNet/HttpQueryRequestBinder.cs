using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet;

/// <summary>
/// Binds HTTP query-string values to request DTOs using the same naming rules as Cohesive API HTTP projections.
/// </summary>
public static class HttpQueryRequestBinder
{
    /// <summary>
    /// Binds the request query string to <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">Request DTO type.</typeparam>
    /// <param name="httpContext">HTTP context containing the query string and JSON options.</param>
    /// <returns>A request DTO populated from query-string values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">
    /// A query value cannot be converted to its declared type, or the request type cannot be materialized.
    /// </exception>
    /// <exception cref="JsonException">The converted query values cannot be deserialized as <typeparamref name="TRequest"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TRequest"/> or one of its values is unsupported by the configured JSON serializer.
    /// </exception>
    public static TRequest Bind<TRequest>(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return Bind<TRequest>(
                httpContext.Request.Query,
                HttpRequestBindingSupport.ResolveJsonOptions(httpContext))
            ?? throw new BadHttpRequestException(
                $"Query request type '{typeof(TRequest).Name}' could not be bound.");
    }

    /// <summary>
    /// Binds the query string to <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">Request DTO type.</typeparam>
    /// <param name="query">Query values to bind.</param>
    /// <param name="jsonOptions">JSON options used to materialize the DTO.</param>
    /// <returns>A request DTO populated from query-string values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">A query value cannot be converted to its declared type.</exception>
    /// <exception cref="JsonException">The converted query values cannot be deserialized as <typeparamref name="TRequest"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TRequest"/> or one of its values is unsupported by the configured JSON serializer.
    /// </exception>
    public static TRequest? Bind<TRequest>(IQueryCollection query, JsonSerializerOptions? jsonOptions = null) =>
        (TRequest?)Bind(query, typeof(TRequest), jsonOptions, returnNullWhenEmpty: false);

    /// <summary>
    /// Binds the request query string to a DTO type, returning <see langword="null"/> when no matching query values exist.
    /// </summary>
    /// <param name="httpContext">HTTP context containing the query string and JSON options.</param>
    /// <param name="requestType">Request DTO type.</param>
    /// <returns>A bound request DTO, or <see langword="null"/> when no matching values are present.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">A query value cannot be converted to its declared type.</exception>
    /// <exception cref="JsonException">The converted query values cannot be deserialized as <paramref name="requestType"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="requestType"/> or one of its values is unsupported by the configured JSON serializer.
    /// </exception>
    public static object? BindOrNull(HttpContext httpContext, Type requestType)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(requestType);
        return Bind(
            httpContext.Request.Query,
            requestType,
            HttpRequestBindingSupport.ResolveJsonOptions(httpContext),
            returnNullWhenEmpty: true);
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

            values[ResolveJsonPropertyName(property)] = HttpRequestBindingSupport.ConvertQueryValue(
                name,
                rawValues,
                property.PropertyType);
        }

        if (values.Count == 0 && returnNullWhenEmpty)
            return null;

        var resolvedJsonOptions = HttpRequestBindingSupport.ResolveJsonOptions(jsonOptions);
        var json = JsonSerializer.SerializeToElement(values, resolvedJsonOptions);
        return json.Deserialize(requestType, resolvedJsonOptions);
    }

    static string ResolveQueryParameterName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? ToSnakeCase(property.Name);

    static string ResolveJsonPropertyName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? property.Name;

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
