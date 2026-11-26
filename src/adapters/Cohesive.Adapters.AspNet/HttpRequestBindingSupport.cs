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

    internal readonly record struct ReadResult<T>(T Value, IResult? Failure = null);

    /// <summary>Prepare the two public decoding failures once, using the operation's response contract.</summary>
    internal class BodyReader(ApiOperation operation)
    {
        internal bool HasQuery => operation.Http?.Query is not null;
        internal string Name => operation.Name;

        internal ValueTask<ReadResult<object?>> ReadRequestAsync(HttpContext http, CancellationToken ct) =>
            operation.Http?.Body is null ? ValueTask.FromResult(new ReadResult<object?>(ReadOperationQuery(http, operation))) : ReadAsync(http, ct);

        internal virtual ValueTask<ReadResult<object?>> ReadAsync(HttpContext http, CancellationToken ct) => default;
    }

    sealed class JsonBodyReader(ApiOperation operation, HttpBodyBinding body, Func<IResult> invalid,
        Func<IResult> unsupported) : BodyReader(operation)
    {
        internal override async ValueTask<ReadResult<object?>> ReadAsync(HttpContext http, CancellationToken ct)
        {
            if (!http.Request.HasJsonContentType())
                return new(null, unsupported());
            try
            {
                var value = await JsonSerializer.DeserializeAsync(http.Request.Body, body.BodyType,
                    ResolveJsonOptions(http), ct).ConfigureAwait(false);
                return new(value, value is null ? invalid() : null);
            }
            catch (JsonException)
            {
                return new(null, invalid());
            }
        }
    }

    internal static BodyReader Prepare(ApiOperation operation, IResult? invalidOverride = null)
    {
        if (operation.Http?.Body is not { } body) return new(operation);
        Func<IResult> invalid = invalidOverride is not null ? () => invalidOverride :
            Failure(ApiResultKind.ValidationFailed, "http.invalidJson", "A non-null JSON body matching the declared request type is required.");
        var unsupported = Failure(ApiResultKind.UnsupportedMediaType, "http.unsupportedMediaType", "A JSON content type is required.");
        return new JsonBodyReader(operation, body, invalid, unsupported);

        Func<IResult> Failure(ApiResultKind kind, string code, string message)
        {
            var status = ApiHttpResultConventions.DefaultStatusCode(kind, typeof(void));
            var candidates = operation.Results.Where(r => r.Kind == kind && r.Http?.StatusCode == status).ToArray();
            var types = candidates.Select(r => r.BodyType).ToHashSet();
            if (operation.Results.Any(r => r.Http?.StatusCode == status && r.Kind != kind && !types.Contains(r.BodyType)))
                throw new InvalidOperationException($"HTTP {status} on '{operation.Id}' declares conflicting response bodies for {kind}.");
            if (types.Contains(typeof(ApiValidationProblem)))
            {
                var media = candidates.First(r => r.BodyType == typeof(ApiValidationProblem)).Http!.ContentType;
                var problem = new ApiValidationProblem(code, message, []) { Status = status, Title = message };
                return () => Results.Json(problem, contentType: media, statusCode: status);
            }
            if (types.Contains(typeof(Cohesive.Api.Execution.ExecutionApiProblem)))
                return () => Results.Json(new Cohesive.Api.Execution.ExecutionApiProblem(code), statusCode: status);
            if (types.Contains(typeof(ApiProblem)))
                return () => Results.Json(new ApiProblem(code, message), statusCode: status);
            if (types.Contains(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails)))
                return () => TypedResults.Problem(statusCode: status, title: message,
                    extensions: new Dictionary<string, object?> { ["code"] = code });
            throw new InvalidOperationException($"JSON body on '{operation.Id}' requires a declared {kind} problem response.");
        }
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
        var http = RequireHttp(operation);
        if (http.Query is { } query)
            return HttpQueryRequestBinder.BindOrNull(httpContext, query.QueryType);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < http.Parameters.Count; i++)
        {
            var parameter = http.Parameters[i];
            if (parameter.Source != HttpParameterSource.Query
                || !httpContext.Request.Query.TryGetValue(parameter.Name, out var rawValues))
            {
                continue;
            }

            values[parameter.Name] = ConvertQueryValue(parameter.Name, rawValues, parameter.Type);
        }

        return values.Count == 0 ? null : values;
    }

    static HttpBinding RequireHttp(ApiOperation operation) =>
        operation.Http ?? throw new InvalidOperationException(
            $"API operation '{operation.Id}' does not declare an HTTP projection.");

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
