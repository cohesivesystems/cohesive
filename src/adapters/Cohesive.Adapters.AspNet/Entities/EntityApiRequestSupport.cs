using System.Globalization;
using System.Text.Json;
using Cohesive.Api;
using Cohesive.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Cohesive.Adapters.AspNet.Entities;

static class EntityApiRequestSupport
{
    static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEntityRepository ResolveRepository(HttpContext httpContext, EntityApiEndpointOptions options) =>
        options.RepositoryResolver(httpContext.RequestServices, options.Entity);

    public static IEntityQueryRepository ResolveQueryRepository(HttpContext httpContext, EntityApiEndpointOptions options) =>
        options.QueryRepositoryResolver(httpContext.RequestServices, options.Entity);

    public static string GetRequiredEntityId(HttpContext httpContext, EntityApiEndpointOptions options)
    {
        if (httpContext.Request.RouteValues.TryGetValue(options.EntityIdRouteParameter, out var value)
            && value is not null
            && !string.IsNullOrWhiteSpace(value.ToString()))
        {
            return value.ToString()!;
        }

        throw new BadHttpRequestException($"Route value '{options.EntityIdRouteParameter}' is required.");
    }

    public static async ValueTask<object?> ReadBodyRequestAsync(HttpContext httpContext, ApiOperation operation, CancellationToken ct)
    {
        if (operation.Http.Body is null)
            return null;

        var request = await JsonSerializer.DeserializeAsync(
                httpContext.Request.Body,
                operation.Http.Body.BodyType,
                ResolveJsonOptions(httpContext),
                ct)
            .ConfigureAwait(false);
        return request ?? throw new BadHttpRequestException($"Request body for operation '{operation.Name}' is required.");
    }

    public static object? ReadQueryRequest(HttpContext httpContext, ApiOperation operation)
    {
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

    public static async Task<EntitySnapshot> CommitAsync(
        EntityApiCommitContext context,
        EntityApiEndpointOptions options,
        EntityConcurrencyToken? expectedConcurrencyToken,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages
        )
    {
        var write = new EntityWriteRequest(context.NewState.Observation, expectedConcurrencyToken);
        if (createOutboxMessages is not null)
        {
            var outboxRepository = options.OutboxRepositoryResolver(context.HttpContext.RequestServices, options.Entity);
            var commit = await outboxRepository.UpsertWithOutbox(
                    context.OperationContext,
                    new EntityOutboxCommit(write, createOutboxMessages(context)))
                .ConfigureAwait(false);
            return commit.Entity;
        }

        return await context.Repository.Upsert(context.OperationContext, write).ConfigureAwait(false);
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
