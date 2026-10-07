using System.Net;
using System.Net.Http.Json;
using Cohesive.Adapters.AspNet;
using Cohesive.Api;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Microsoft.AspNetCore.Builder;

namespace Cohesive.Tests.Api;

public sealed class ApiQueryBindingTests
{
    public sealed record Detail(string Id);

    [Fact]
    public async Task Typed_query_binds_route_and_maps_success_missing_and_invalid_input()
    {
        await using var app = WebApplication.CreateBuilder().Build();
        var calls = 0;
        var read = new DetailReader((id, token) =>
        {
            calls++;
            Assert.True(token.CanBeCanceled);
            return Task.FromResult(id == "1" ? new Detail(id) : null);
        });
        var endpoint = Cohesive.Api.Api.Define().Entity<Detail>().Query("Details")
            .Route("GET", "/details/{id}").RouteParameter<string>("id")
            .Returns<Detail>().Result(ApiResultKind.NotFound).Build<Detail>();
        var binding = app.MapApiQuery(endpoint, read).FromRoute<int>("id", id => id.ToString());
        binding.OkOrNotFound();
        Assert.Throws<InvalidOperationException>(() => binding.OkOrNotFound());
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal("1", (await client.GetFromJsonAsync<Detail>("/details/1"))!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/details/2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/details/no-number")).StatusCode);
        Assert.Equal(2, calls);
        await app.StopAsync();
    }

    [Fact]
    public async Task Registration_rejects_wrong_result_unknown_route_and_missing_response_policy()
    {
        await using var app = WebApplication.CreateBuilder().Build();
        var read = new DetailReader((_, _) => Task.FromResult<Detail?>(null));
        var wrong = Cohesive.Api.Api.Define().Entity<Detail>().Query("Wrong")
            .Route("GET", "/wrong/{id}").RouteParameter<string>("id").Returns<string>();
        Assert.Throws<ArgumentException>(() => wrong.Build<Detail>());
        var query = Cohesive.Api.Api.Define().Entity<Detail>().Query("Details")
            .Route("GET", "/details/{id}").RouteParameter<string>("id").Returns<Detail>().Build<Detail>();
        Assert.Throws<ArgumentException>(() => app.MapApiQuery(query, read).FromRoute<int>("other", id => id.ToString()));
        Assert.Throws<InvalidOperationException>(() => app.MapApiQuery(query, read).OkOrNotFound());
        Assert.Throws<InvalidOperationException>(() => app.MapApiQuery(query, read)
            .FromRoute<int>("id", id => id.ToString()).OkOrNotFound());
    }
    // The HTTP boundary depends only on the typed contract, not the PostgreSQL adapter or a method group.
    sealed class DetailReader(Func<string, CancellationToken, Task<Detail?>> read) : IRelationQueryReader<string, Detail?>
    {
        public RelationQuery<string, Detail?> Definition { get; } = CreateDefinition();
        public Task<Detail?> ReadAsync(string input, CancellationToken cancellationToken = default) => read(input, cancellationToken);

        static RelationQuery<string, Detail?> CreateDefinition()
        {
            var author = RelationQuery.Expression();
            var parameter = author.Parameter<string>("id");
            var rows = author.Where(author.Source(author.Clr.Shape<Detail>()), detail => detail.Id == parameter.Value);
            return author.BuildQuery(new("details"), new("Details"), rows, parameter,
                result: values => values.Count == 0 ? null : values[0]);
        }
    }

}
