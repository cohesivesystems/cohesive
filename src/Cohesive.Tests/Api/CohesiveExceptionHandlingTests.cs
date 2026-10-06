using System.Net;
using System.Text.Json;
using Cohesive.Adapters.AspNet;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class CohesiveExceptionHandlingTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task Native_pipeline_reports_sanitized_conflict_without_retry(string accept)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveExceptionHandling();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        var calls = 0;
        app.MapGet("/conflict", (Func<IResult>)(() =>
        {
            calls++;
            throw new ObservationConcurrencyConflictException("private backend identity and token");
        }));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        var response = await client.GetAsync("/conflict");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private backend", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(409, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("services.concurrency.conflict", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("traceId").GetString()));
        Assert.Equal(1, calls);
        await app.StopAsync();
    }

    [Fact]
    public async Task Unknown_exception_falls_through_without_touching_response()
    {
        var services = new ServiceCollection().AddLogging().AddCohesiveExceptionHandling();
        await using var provider = services.BuildServiceProvider();
        var handler = Assert.Single(provider.GetServices<IExceptionHandler>());
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.StatusCode = 202;
        Assert.False(await handler.TryHandleAsync(context, new InvalidOperationException("unrelated"), default));
        Assert.Equal(202, context.Response.StatusCode);
        Assert.Null(context.Response.ContentType);
    }
}
