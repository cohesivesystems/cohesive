using System.Net;
using System.Text.Json;
using Cohesive.Adapters.AspNet;
using Cohesive.Storage;
using Cohesive.Transitions.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class CohesiveExceptionHandlingTests
{
    [Theory]
    [InlineData("application/json", false)]
    [InlineData("text/plain", false)]
    [InlineData("application/json", true)]
    [InlineData("text/plain", true)]
    public async Task Native_pipeline_reports_sanitized_failure_without_retry(string accept, bool preparation)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveExceptionHandling();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        var calls = 0;
        app.MapGet("/conflict", (Func<IResult>)(() =>
        {
            calls++;
            if (preparation)
                throw new TransitionStatePreparationException("transition.state.versionOverflow", "/current/version",
                    "private backend identity and token", new InvalidOperationException("private inner cause"));
            throw new ObservationConcurrencyConflictException("private backend identity and token");
        }));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        var response = await client.GetAsync("/conflict");
        Assert.Equal(preparation ? HttpStatusCode.InternalServerError : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private backend", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(preparation ? 500 : 409, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(preparation ? "transition.state.versionOverflow" : "services.concurrency.conflict", json.RootElement.GetProperty("code").GetString());
        if (preparation)
        {
            Assert.Equal("/current/version", json.RootElement.GetProperty("location").GetString());
            Assert.Equal(TransitionStatePreparationException.SafeMessage, json.RootElement.GetProperty("detail").GetString());
            Assert.DoesNotContain("private inner", body);
        }
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
