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
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task Native_pipeline_reports_sanitized_concurrency_conflict_without_retry(string accept)
    {
        var (status, problem) = await InvokeFailure(accept,
            new ObservationConcurrencyConflictException("private backend identity and token"));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("services.concurrency.conflict", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task Native_pipeline_reports_sanitized_preparation_failure_without_retry(string accept)
    {
        var (status, problem) = await InvokeFailure(accept,
            new TransitionStatePreparationException("transition.state.versionOverflow", "/current/version",
                "private backend identity and token", new InvalidOperationException("private inner cause")));
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.Equal("transition.state.versionOverflow", problem.GetProperty("code").GetString());
        Assert.Equal("/current/version", problem.GetProperty("location").GetString());
        Assert.Equal(TransitionStatePreparationException.SafeMessage, problem.GetProperty("detail").GetString());
    }

    static async Task<(HttpStatusCode Status, JsonElement Problem)> InvokeFailure(string accept, Exception failure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveExceptionHandling();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        var calls = 0;
        app.MapGet("/failure", (Func<IResult>)(() =>
        {
            calls++;
            throw failure;
        }));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await client.GetAsync("/failure");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private backend", body);
        Assert.DoesNotContain("private inner", body);
        using var json = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("traceId").GetString()));
        Assert.Equal(1, calls);
        await app.StopAsync();
        return (response.StatusCode, json.RootElement.Clone());
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
