using System.Net;
using System.Net.Http.Json;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Tests.Api;

public sealed class TypedEntityApiBindingsTests
{
    public sealed record Order(string Id, string Partition, string Status);
    public sealed record Submit(string Id);
    public sealed record Outcome(string Status);
    static readonly Cohesive.Transitions.Model.EntityDefinition Entity = ObjectEntityDefinition.For<Order>(new("typed/order"));
    static readonly Transition<Order, Submit, Outcome> SubmitTransition = TransitionAuthoring.Create<Order, Submit, Outcome>(
        Entity.Shape, new("typed/submit"), new("1"), transition => transition
            .Requires((state, input) => state.Id == input.Id && state.Status == "Draft", (state, _) => new Outcome(state.Status))
            .Set(state => state.Status, "Submitted").Return(new Outcome("Submitted")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Combined_and_separate_declarations_share_typed_execution(bool separate)
    {
        var repository = new InMemoryEntityOutboxRepository(Entity, partitionKeyFieldName: "Partition");
        TypedEntityApiBindings<Order>? retained = null;
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRequestOperationContext();
        await using var app = builder.Build();
        app.UseRequestOperationContext();
        app.MapEntityApi<Order>(Entity, repository, "configured-partition", bindings =>
        {
            retained = bindings;
            if (separate)
            {
                var create = Cohesive.Api.Api.Define().Entity<Order>().Command("Create").Route("POST", "/orders").Returns<Order>(ApiResultKind.Created).Build();
                var get = Cohesive.Api.Api.Define().Entity<Order>().Query("Get").Route("GET", "/orders/{id}").RouteParameter<string>("id").Returns<Order>().Build();
                var submit = Cohesive.Api.Api.Define().Entity<Order>().Command("Submit").Route("POST", "/orders/{id}/submit")
                    .RouteParameter<string>("id").Returns<Order>().Result<Outcome>(ApiResultKind.Conflict).Transition(SubmitTransition.Reference).Build();
                bindings.Create(create, partition => new Order("one", partition, "Draft"), state => state.Id, state => TypedResults.Created("/orders/one", state))
                    .Get(get, state => TypedResults.Ok(state))
                    .Transition(submit, SubmitTransition).Input(request => new Submit(request.RequiredEntityId))
                    .OnApplied((state, outcome) => TypedResults.Ok(state)).OnRejected(outcome => TypedResults.Conflict(outcome));
            }
            else
            {
                bindings.Create("Create", "/orders", partition => new Order("one", partition, "Draft"), state => state.Id, state => TypedResults.Created("/orders/one", state))
                    .Get("Get", "/orders/{id}", state => TypedResults.Ok(state))
                    .Transition("Submit", "/orders/{id}/submit", SubmitTransition).Input(request => new Submit(request.RequiredEntityId))
                    .OnApplied((state, outcome) => TypedResults.Ok(state)).OnRejected(outcome => TypedResults.Conflict(outcome));
            }
        });
        Assert.Throws<InvalidOperationException>(() => retained!.Map(app));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/orders", null)).StatusCode);
        var created = await client.GetFromJsonAsync<Order>("/orders/one");
        Assert.Equal("Draft", created!.Status);
        Assert.Equal("configured-partition", created.Partition);
        var submitted = await client.PostAsync("/orders/one/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.Equal("Submitted", (await submitted.Content.ReadFromJsonAsync<Order>())!.Status);
        var rejected = await client.PostAsync("/orders/one/submit", null);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("Submitted", (await rejected.Content.ReadFromJsonAsync<Outcome>())!.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/orders/missing")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void Create_if_absent_requires_native_capability_during_registration()
    {
        var bindings = new TypedEntityApiBindings<Order>(Entity, new UnsupportedCreationRepository(), "local");
        var endpoint = Cohesive.Api.Api.Define().Entity<Order>().Command("Create").Route("POST", "/orders")
            .Returns<Order>(ApiResultKind.Created).Build();
        Assert.Throws<NotSupportedException>(() => bindings.CreateIfAbsent(endpoint,
            partition => new Order("one", partition, "Draft"), value => value.Id,
            value => TypedResults.Created("/orders/one", value)));
    }

    sealed class UnsupportedCreationRepository : IEntityRepository
    {
        public Cohesive.Transitions.Model.EntityDefinition EntityDefinition => Entity;
        public string? IdentityField => "Id";
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) =>
            throw new InvalidOperationException("Registration must not perform reads.");
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) =>
            throw new InvalidOperationException("Registration must not perform writes.");
    }

    [Fact]
    public async Task Create_if_absent_returns_conflict_without_overwriting_existing_state()
    {
        var repository = new InMemoryEntityOutboxRepository(Entity, partitionKeyFieldName: "Partition");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRequestOperationContext();
        await using var app = builder.Build();
        app.UseRequestOperationContext();
        var endpoint = Cohesive.Api.Api.Define().Entity<Order>().Command("Create").Route("POST", "/orders")
            .Returns<Order>(ApiResultKind.Created).Build();
        app.MapEntityApi<Order>(Entity, repository, "local", bindings => bindings.CreateIfAbsent(endpoint,
            partition => new Order("one", partition, "Draft"), value => value.Id,
            value => TypedResults.Created("/orders/one", value)));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/orders", null)).StatusCode);
        var before = await repository.TryGet(OperationContext.Create(), "one", new(partitionKey: "local"));
        var rejected = await client.PostAsync("/orders", null);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains(ApiProblemCodes.ConcurrencyConflict, await rejected.Content.ReadAsStringAsync());
        Assert.Equal(before, await repository.TryGet(OperationContext.Create(), "one", new(partitionKey: "local")));
        Assert.Equal(0, before!.Entity.Version);
        await app.StopAsync();
    }

    [Fact]
    public async Task Registration_rejects_mismatched_response_duplicate_and_incomplete_binding()
    {
        var repository = new InMemoryEntityOutboxRepository(Entity, partitionKeyFieldName: "Partition");
        var bindings = new TypedEntityApiBindings<Order>(Entity, repository, "local");
        var wrong = Cohesive.Api.Api.Define().Entity<Order>().Query("Get").Route("GET", "/orders/{id}").Returns<string>().Build();
        Assert.Throws<ArgumentException>(() => bindings.Get(wrong, state => TypedResults.Ok(state)));
        var good = Cohesive.Api.Api.Define().Entity<Order>().Query("Get").Route("GET", "/orders/{id}").Returns<Order>().Build();
        var foreign = Cohesive.Api.Api.Define().Entity<Outcome>().Query("Foreign").Route("GET", "/foreign/{id}").Returns<Order>().Build();
        Assert.Throws<ArgumentException>(() => bindings.Get(foreign, state => TypedResults.Ok(state)));
        var wrongTransition = Cohesive.Api.Api.Define().Entity<Order>().Command("Submit").Route("POST", "/orders/{id}/submit").Returns<Order>().Build();
        Assert.Throws<ArgumentException>(() => new TypedEntityApiBindings<Order>(Entity, repository, "local")
            .Transition(wrongTransition, SubmitTransition).Input(request => new Submit(request.RequiredEntityId))
            .OnApplied((state, outcome) => TypedResults.Ok(state)));
        bindings.Get(good, state => TypedResults.Ok(state));
        Assert.Throws<InvalidOperationException>(() => bindings.Get(good, state => TypedResults.Ok(state)));
        var incomplete = bindings.Transition("Submit", "/orders/{id}/submit", SubmitTransition)
            .Input(request => new Submit(request.RequiredEntityId));
        Assert.Throws<InvalidOperationException>(() => incomplete.Input(request => new Submit(request.RequiredEntityId)));
        await using var app = WebApplication.CreateBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => bindings.Map(app));
    }
}
