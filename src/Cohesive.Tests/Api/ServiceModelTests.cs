using System.Net.Http.Json;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class ServiceModelTests
{
    public sealed record Detail(string Id);
    sealed class Catalog : ApiModel
    {
        internal Catalog(bool duplicateRoute = false)
        {
            First = Endpoints.Query("first").Route("GET", "/first/{id}").RouteParameter<string>("id")
                .Result(ApiResultKind.NotFound).Build<Detail>();
            Second = Endpoints.Query("second").Route("GET", duplicateRoute ? "/first/{id}" : "/second/{id}")
                .RouteParameter<string>("id").Result(ApiResultKind.NotFound).Build<Detail>();
        }
        internal ApiEndpoint<Detail> First { get; }
        internal ApiEndpoint<Detail> Second { get; }
    }
    static RelationQuery<string, Detail?> Query(string id)
    {
        var query = RelationQuery.Expression();
        var parameter = query.Parameter<string>("id");
        var rows = query.Where(query.Source(query.Clr.Shape<Detail>()), item => item.Id == parameter.Value);
        return query.BuildQuery(new(id), new(id), rows, parameter, result: values => values.Count == 0 ? null : values[0]);
    }
    static void First(ServiceApiImplementation<Catalog> bindings) => bindings.Bind(api => api.First, Query("first"), new("1"), 10, 1000,
        query => query.FromRoute<string>("id", value => value).OkOrNotFound());
    static void Second(ServiceApiImplementation<Catalog> bindings) => bindings.Bind(api => api.Second, Query("second"), new("1"), 10, 1000,
        query => query.FromRoute<string>("id", value => value).OkOrNotFound());

    [Fact]
    public async Task Partial_contributions_map_one_surface_and_prepare_once_before_requests()
    {
        var api = new Catalog();
        var builder = WebApplication.CreateBuilder();
        var persistence = new Persistence();
        var model = new ServiceModelBuilder().Implement(api, Second).Implement(api, First);
        Assert.Equal(2, model.QueryDependencies.Queries.Length);
        var prepared = model.Build(builder.Services, persistence);
        Assert.Equal(2, persistence.Prepared);
        await using var app = builder.Build();
        app.MapServices(prepared);
        Assert.Throws<InvalidOperationException>(() => app.MapServices(prepared));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal("one", (await client.GetFromJsonAsync<Detail>("/first/one"))!.Id);
        Assert.Equal("two", (await client.GetFromJsonAsync<Detail>("/second/two"))!.Id);
        Assert.Equal(2, persistence.Prepared);
        await app.StopAsync();
    }

    [Fact]
    public void Missing_implementations_are_reported_at_setup()
    {
        var api = new Catalog();
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceModelBuilder()
            .Implement(api, First).Build(new ServiceCollection(), new Persistence()));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.missingImplementation" && item.Message.Contains(api.Second.Id.Value));
    }

    [Fact]
    public void Selectors_cannot_capture_another_api_instance()
    {
        var api = new Catalog(); var other = new Catalog();
        Assert.Throws<ArgumentException>(() => new ServiceModelBuilder().Implement(api, bindings =>
            bindings.Bind(_ => other.First, Query("first"), new("1"), 10, 1000, _ => { })));
    }

    [Fact]
    public async Task Duplicate_routes_fail_without_publishing_partial_routes()
    {
        var api = new Catalog(duplicateRoute: true);
        var builder = WebApplication.CreateBuilder();
        var prepared = new ServiceModelBuilder().Implement(api, First).Implement(api, Second).Build(builder.Services, new Persistence());
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.MapServices(prepared));
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources);
    }

    [Fact]
    public async Task Incomplete_query_binding_publishes_no_routes()
    {
        var api = new Catalog();
        var builder = WebApplication.CreateBuilder();
        var prepared = new ServiceModelBuilder().Implement(api, First).Implement(api, bindings =>
            bindings.Bind(a => a.Second, Query("second"), new("1"), 10, 1000, _ => { }))
            .Build(builder.Services, new Persistence());
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.MapServices(prepared));
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources);
    }

    [Fact]
    public void Separately_declared_catalog_and_late_contribution_rules_are_supported()
    {
        var api = new Catalog();
        var builder = new ServiceModelBuilder().Implement(api, api.Definition, First).Implement(api, api.Definition, Second);
        _ = builder.Build(new ServiceCollection(), new Persistence());
        Assert.Throws<InvalidOperationException>(() => builder.Implement(api, First));
    }

    [Fact]
    public void Competing_partial_implementations_fail_before_routes_are_published()
    {
        var api = new Catalog();
        var query = Query("first");
        void Contribute(ServiceApiImplementation<Catalog> bindings) => bindings.Bind(a => a.First, query, new("1"), 10, 1000,
            binding => binding.FromRoute<string>("id", value => value).OkOrNotFound());
        var builder = new ServiceModelBuilder().Implement(api, Contribute).Implement(api, Contribute).Implement(api, Second);
        var error = Assert.Throws<ServiceBindingValidationException>(() => builder.Build(new ServiceCollection(), new Persistence()));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.duplicateImplementation");
    }

    [Fact]
    public void Competing_query_preparation_bounds_are_rejected()
    {
        var api = new Catalog();
        var query = Query("shared");
        var model = new ServiceModelBuilder().Implement(api, bindings => bindings.Bind(a => a.First, query, new("1"), 10, 1000, _ => { }));
        Assert.Throws<ArgumentException>(() => model.Implement(api, bindings => bindings.Bind(a => a.Second, query, new("1"), 11, 1000, _ => { })));
    }

    sealed class Persistence : IServicePersistence
    {
        internal int Prepared;
        public IEntityRepository Repository(ValueContract observation) => throw new InvalidOperationException("Query composition must not resolve an entity repository.");
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query, int maximumRows, long maximumBytes)
        {
            Prepared++;
            return (IRelationQueryReader<TInput, TResult>)(object)new Reader((RelationQuery<string, Detail?>)(object)query);
        }
    }
    sealed class Reader(RelationQuery<string, Detail?> definition) : IRelationQueryReader<string, Detail?>
    {
        public RelationQuery<string, Detail?> Definition => definition;
        public Task<Detail?> ReadAsync(string input, CancellationToken cancellationToken = default) => Task.FromResult<Detail?>(new(input));
    }
}
