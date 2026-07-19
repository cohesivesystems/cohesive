using System.Globalization;
using System.Text.Json;
using Cohesive.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Cohesive.Adapters.AspNet;

/// <summary>
/// Shared request-binding mechanics for ASP.NET interpretations of declared Cohesive API operations.
/// </summary>
internal static class HttpRequestBindingSupport
{
    static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Reads the body or query input declared by an API operation.</summary>
    /// <param name="httpContext">Current HTTP request.</param>
    /// <param name="operation">API operation whose HTTP input is being interpreted.</param>
    /// <param name="cancellationToken">Token that cancels asynchronous body deserialization.</param>
    /// <returns>The bound request value, or <see langword="null"/> when no declared input has a value.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">A required body is absent or a query value cannot be converted.</exception>
    public static async ValueTask<object?> ReadOperationRequestAsync(
        HttpContext httpContext,
        ApiOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(operation);

        return operation.Http.Body is not null
            ? await ReadOperationBodyAsync(httpContext, operation, cancellationToken).ConfigureAwait(false)
            : ReadOperationQuery(httpContext, operation);
    }

    /// <summary>Reads the body declared by an API operation.</summary>
    /// <param name="httpContext">Current HTTP request.</param>
    /// <param name="operation">API operation whose body is being interpreted.</param>
    /// <param name="cancellationToken">Token that cancels body deserialization.</param>
    /// <returns>The deserialized body, or <see langword="null"/> when the operation declares no body.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">The operation declares a body but deserialization returns no value.</exception>
    public static async ValueTask<object?> ReadOperationBodyAsync(
        HttpContext httpContext,
        ApiOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Http.Body is not { } body)
            return null;

        var request = await JsonSerializer.DeserializeAsync(
                httpContext.Request.Body,
                body.BodyType,
                ResolveJsonOptions(httpContext),
                cancellationToken)
            .ConfigureAwait(false);
        return request ?? throw new BadHttpRequestException(
            $"Request body for operation '{operation.Name}' is required.");
    }

    /// <summary>Reads the query input declared by an API operation.</summary>
    /// <param name="httpContext">Current HTTP request.</param>
    /// <param name="operation">API operation whose query input is being interpreted.</param>
    /// <returns>A bound query model or parameter dictionary, or <see langword="null"/> when no values match.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="BadHttpRequestException">A query value cannot be converted to its declared type.</exception>
    public static object? ReadOperationQuery(HttpContext httpContext, ApiOperation operation)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Http.Query is { } query)
            return HttpQueryRequestBinder.BindOrNull(httpContext, query.QueryType);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < operation.Http.Parameters.Count; i++)
        {
            var parameter = operation.Http.Parameters[i];
            if (parameter.Source != HttpParameterSource.Query
                || !httpContext.Request.Query.TryGetValue(parameter.Name, out var rawValues))
            {
                continue;
            }

            values[parameter.Name] = ConvertQueryValue(parameter.Name, rawValues, parameter.Type);
        }

        return values.Count == 0 ? null : values;
    }

    /// <summary>Converts raw query values to a declared scalar or sequence type.</summary>
    /// <param name="parameterName">Query parameter name used in conversion diagnostics.</param>
    /// <param name="values">Raw query values.</param>
    /// <param name="targetType">Declared scalar or sequence type.</param>
    /// <returns>The converted scalar, an array of converted elements, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="parameterName"/> is empty.</exception>
    /// <exception cref="BadHttpRequestException">A raw value cannot be converted to the declared type.</exception>
    public static object? ConvertQueryValue(
        string parameterName,
        IReadOnlyList<string?> values,
        Type targetType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(targetType);

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
                array.SetValue(ConvertScalar(parameterName, normalizedValues[i], elementType), i);
            return array;
        }

        var value = values.Count == 0 ? null : values[^1];
        return ConvertScalar(parameterName, value, normalizedType, allowsNull);
    }

    /// <summary>Resolves request-scoped HTTP JSON serializer options.</summary>
    /// <param name="httpContext">Current HTTP request.</param>
    /// <returns>Configured HTTP JSON options, or deterministic web defaults.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpContext"/> is <see langword="null"/>.</exception>
    public static JsonSerializerOptions ResolveJsonOptions(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.RequestServices?.GetService<IOptions<HttpJsonOptions>>()?.Value.SerializerOptions
            ?? DefaultJsonOptions;
    }

    /// <summary>Uses explicitly supplied JSON options or the shared web defaults.</summary>
    /// <param name="jsonOptions">Explicit serializer options, or <see langword="null"/>.</param>
    /// <returns>The effective serializer options.</returns>
    public static JsonSerializerOptions ResolveJsonOptions(JsonSerializerOptions? jsonOptions) =>
        jsonOptions ?? DefaultJsonOptions;

    static object? ConvertScalar(
        string parameterName,
        string? value,
        Type targetType,
        bool allowsNull = false)
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
                error);
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
            if (!candidate.IsGenericType
                || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
            {
                continue;
            }

            elementType = candidate.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(void);
        return false;
    }
}
