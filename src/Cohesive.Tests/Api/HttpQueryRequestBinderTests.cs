using System.Text.Json.Serialization;
using Cohesive.Adapters.AspNet;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Cohesive.Tests.Api;

public sealed class HttpQueryRequestBinderTests
{
    [Fact]
    public void Bind_BindsJsonNamedAndSnakeCaseQueryValues()
    {
        var query = CreateQuery(
            ("process_id", "compile-123"),
            ("tenant_ids", "sample-internal"),
            ("tenant_ids", "ui-test"),
            ("status", "Running"),
            ("status", "Completed"),
            ("started_after_utc", "2026-01-02T03:04:05+00:00"),
            ("limit", "25")
            );

        var request = HttpQueryRequestBinder.Bind<QueryBinderSampleRequest>(query);

        Assert.NotNull(request);
        Assert.NotNull(request.TenantIds);
        Assert.NotNull(request.Status);
        Assert.Equal("compile-123", request.ProcessId);
        Assert.Equal(["sample-internal", "ui-test"], request.TenantIds);
        Assert.Equal(["Running", "Completed"], request.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), request.StartedAfterUtc);
        Assert.Equal(25, request.Limit);
    }

    [Fact]
    public void BindOrNull_ReturnsNullWhenNoMatchingQueryValuesExist()
    {
        var context = new DefaultHttpContext
        {
            Request =
            {
                Query = CreateQuery(("unrelated", "value"))
            }
        };

        var request = HttpQueryRequestBinder.BindOrNull(context, typeof(QueryBinderSampleRequest));

        Assert.Null(request);
    }

    [Fact]
    public void Bind_ThrowsBadRequestForInvalidScalarValue()
    {
        var query = CreateQuery(("limit", "not-an-int"));

        var error = Assert.Throws<BadHttpRequestException>(
            () => HttpQueryRequestBinder.Bind<QueryBinderSampleRequest>(query));

        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_BindsBlankNullableScalarAsNull()
    {
        var query = CreateQuery(("limit", ""));

        var request = HttpQueryRequestBinder.Bind<QueryBinderSampleRequest>(query);

        Assert.NotNull(request);
        Assert.Null(request.Limit);
    }

    static QueryCollection CreateQuery(params (string Name, string Value)[] values)
    {
        var grouped = values
            .GroupBy(static value => value.Name, static value => value.Value, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new StringValues(group.ToArray()),
                StringComparer.Ordinal);
        return new QueryCollection(grouped);
    }

    sealed record QueryBinderSampleRequest(
        [property: JsonPropertyName("process_id")] string? ProcessId = null,
        [property: JsonPropertyName("tenant_ids")] string[]? TenantIds = null,
        string[]? Status = null,
        [property: JsonPropertyName("started_after_utc")] DateTimeOffset? StartedAfterUtc = null,
        int? Limit = null
        );
}
