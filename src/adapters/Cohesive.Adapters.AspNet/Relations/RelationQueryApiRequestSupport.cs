using System.Globalization;
using System.Text.Json;
using Cohesive.Api;
using Cohesive.Relations.Queries;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Cohesive.Adapters.AspNet.Relations;

static class RelationQueryApiRequestSupport
{
    static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadRepositoryRegistry ResolveRepositoryRegistry(
        HttpContext httpContext,
        RelationQueryApiEndpointOptions options
        ) =>
        options.RepositoryRegistryResolver(httpContext.RequestServices)
        ?? throw new InvalidOperationException("Relation query API endpoint options resolved a null read-repository registry.");

    public static async ValueTask<object?> ReadRequestAsync(HttpContext httpContext, ApiOperation operation, CancellationToken ct)
    {
        if (operation.Http.Body is { } body)
        {
            var request = await JsonSerializer.DeserializeAsync(
                httpContext.Request.Body,
                body.BodyType,
                ResolveJsonOptions(httpContext),
                ct
            ).ConfigureAwait(false);
            return request ?? throw new BadHttpRequestException($"Request body for operation '{operation.Name}' is required.");
        }

        if (operation.Http.Query is { } query)
            return HttpQueryRequestBinder.BindOrNull(httpContext, query.QueryType);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < operation.Http.Parameters.Count; i++)
        {
            var parameter = operation.Http.Parameters[i];
            if (parameter.Source != HttpParameterSource.Query)
                continue;

            if (!httpContext.Request.Query.TryGetValue(parameter.Name, out var rawValues))
                continue;

            values[parameter.Name] = ConvertQueryValue(rawValues, parameter.Type);
        }

        return values.Count == 0 ? null : values;
    }

    static object? ConvertQueryValue(IReadOnlyList<string?> values, Type targetType)
    {
        var normalizedType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (TryGetSequenceElementType(normalizedType, out var elementType))
        {
            var array = Array.CreateInstance(elementType, values.Count);
            for (var i = 0; i < values.Count; i++)
                array.SetValue(ConvertScalar(values[i], elementType), i);
            return array;
        }

        return ConvertScalar(values.Count == 0 ? null : values[^1], normalizedType);
    }

    // TODO: unify into general reflection metadata library
    static object? ConvertScalar(string? value, Type targetType)
    {
        if (value is null)
            return null;

        if (targetType == typeof(string))
            return value;
        if (targetType == typeof(bool))
            return bool.Parse(value);
        if (targetType == typeof(int))
            return int.Parse(value, CultureInfo.InvariantCulture);
        if (targetType == typeof(long))
            return long.Parse(value);
        if (targetType == typeof(decimal))
            return decimal.Parse(value, CultureInfo.InvariantCulture);
        if (targetType == typeof(double))
            return double.Parse(value, CultureInfo.InvariantCulture);
        if (targetType == typeof(float))
            return float.Parse(value, CultureInfo.InvariantCulture);
        if (targetType == typeof(Guid))
            return Guid.Parse(value);
        if (targetType == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
        if (targetType == typeof(DateTime))
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (targetType == typeof(DateOnly))
            return DateOnly.Parse(value, CultureInfo.InvariantCulture);
        if (targetType == typeof(TimeOnly))
            return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
        if (targetType.IsEnum)
            return Enum.Parse(targetType, value, ignoreCase: true);

        return value;
    }

    // TODO: unify into general reflection metadata library
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

    static JsonSerializerOptions ResolveJsonOptions(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IOptions<HttpJsonOptions>>()?.Value.SerializerOptions ?? DefaultJsonOptions;
}
