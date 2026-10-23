using System.Collections.Immutable;
using System.Net.Http.Json;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Host.Composition;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ApplicationServiceCompositionTests
{
    public sealed record Detail(string Id);
    sealed class App : ApplicationModel
    {
        public required ApiBindingModel First { get; init; }
        public required ApiBindingModel Second { get; init; }
        public required StorageModel StoreA { get; init; }
        public required StorageModel StoreB { get; init; }
    }
    sealed class Api : ApiModel
    {
        public Api(string id) => Detail = Endpoints.Query(id).Route("GET", "/" + id + "/{id}")
            .RouteParameter<string>("id").Result(ApiResultKind.NotFound).Build<Detail>();
        public ApiEndpoint<Detail> Detail { get; }
    }
    class Endpoints : ApiBindingModel
    {
        public Endpoints(string id, RelationQuery<string, Detail?> query) => Bindings.Implement(new Api(id), binding =>
            binding.Bind(api => api.Detail, query, new("1"), 10, 1000,
                route => route.FromRoute<string>("id", value => value).OkOrNotFound(), RelationQueryConsistency.SingleSnapshot));
    }
    sealed class First(RelationQuery<string, Detail?> query) : Endpoints("first", query);
    sealed class Second(RelationQuery<string, Detail?> query) : Endpoints("second", query);
    sealed class Duplicate(RelationQuery<string, Detail?> query) : Endpoints("first", query);
    class Store : StorageModel
    {
        public Store(Composition composition) { Storage.Define(() => composition.Bindings[0]); Storage.Compose(() => composition); }
    }
    sealed class StoreA(Composition composition) : Store(composition);
    sealed class StoreB(Composition composition) : Store(composition);
    sealed class Composition(params RelationQueryCompilationRequest[] queries) : IServiceStorageComposition
    {
        public int Calls;
        public Exception? Failure;
        public Task? Wait;
        public Action? OnPrepare;
        public ImmutableArray<object> Bindings { get; } = [new object()];
        public ImmutableArray<ValueContract> Observations { get; init; } = [];
        public ImmutableArray<QualifiedShapeId> Shapes { get; init; } = [.. queries.SelectMany(request =>
            RelationQueryStaticCompiler.Compile(request).Plan!.InputContract.Sources.Select(source => source.Shape)).Distinct()];
        public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> selections) => this;
        public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            Calls++;
            OnPrepare?.Invoke();
            if (Wait is not null) await Wait.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return new Persistence();
        }
    }
    sealed class Persistence : IServicePersistence
    {
        public IEntityRepository Repository(ValueContract observation) => throw new NotSupportedException();
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query,
            int maximumRows, long maximumBytes, RelationQueryConsistency consistency) =>
            (IRelationQueryReader<TInput, TResult>)(object)new Reader((RelationQuery<string, Detail?>)(object)query);
    }
    sealed class Reader(RelationQuery<string, Detail?> query) : IRelationQueryReader<string, Detail?>
    {
        public RelationQueryConsistency Consistency => RelationQueryConsistency.SingleSnapshot;
        public RelationQuery<string, Detail?> Definition => query;
        public Task<Detail?> ReadAsync(string input, CancellationToken cancellationToken = default) => Task.FromResult<Detail?>(new(input));
    }
    static RelationQuery<string, Detail?> Query(string id, bool changed = false)
    {
        var query = RelationQuery.Expression();
        var parameter = query.Parameter<string>("id");
        var rows = query.Where(query.Source(query.Clr.Shape<Detail>()), item => item.Id == parameter.Value);
        if (changed) rows = query.Where(rows, item => item.Id != "excluded");
        return query.BuildQuery(new(id), new(id), rows, parameter, values => values.Count == 0 ? null : values[0]);
    }
    static App Activate(First first, Second second, Composition a, Composition b)
    {
        var declarations = new ServiceCollection().AddSingleton(new App { First = first, Second = second, StoreA = new StoreA(a), StoreB = new StoreB(b) }).AddSingleton(first).AddSingleton(second)
            .AddSingleton(new StoreA(a)).AddSingleton(new StoreB(b));
        return new ApplicationModelBuilder(declarations).Build().Get<App>();
    }

    [Theory]
        [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Multiple_endpoint_and_storage_models_map_with_once_per_host_preparation(bool shared, bool sharedQuery)
    {
        var first = Query("first"); var second = sharedQuery ? Query("first") : Query("second");
        var a = new Composition(shared && !sharedQuery ? [first.CompilationRequest, second.CompilationRequest] : [first.CompilationRequest]);
        var b = shared ? a : new Composition(second.CompilationRequest);
        var model = Activate(new(first), new(second), a, b);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveServices(model);
        Assert.Equal(0, a.Calls); Assert.Equal(0, b.Calls);
        await using var app = builder.Build();
        await app.MapServicesAsync();
        Assert.Equal(1, a.Calls); Assert.Equal(1, b.Calls);
        await Assert.ThrowsAsync<ServiceMappingException>(() => app.MapServicesAsync());
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        Assert.Equal("one", (await client.GetFromJsonAsync<Detail>("/first/one"))!.Id);
        Assert.Equal("two", (await client.GetFromJsonAsync<Detail>("/second/two"))!.Id);
        Assert.Equal(1, a.Calls); Assert.Equal(1, b.Calls);
        await app.StopAsync();
    }

    [Fact]
    public void Missing_or_ambiguous_query_ownership_fails_before_registration_or_IO()
    {
        var first = Query("first"); var second = Query("second");
        foreach (var ambiguous in new[] { false, true })
        {
            var a = new Composition(ambiguous ? [first.CompilationRequest] : []);
            var b = new Composition(ambiguous ? [second.CompilationRequest] : []);
            var model = Activate(new(first), new(second), a, b);
            var services = new ServiceCollection();
            var error = Assert.Throws<ServiceBindingValidationException>(() => services.AddCohesiveServices(model));
            Assert.Equal(2, error.Validation.Diagnostics.Length);
            Assert.All(error.Validation.Diagnostics, diagnostic => Assert.Equal(ambiguous ? "services.storage.ambiguousQuery" : "services.storage.queryMissing", diagnostic.Code));
            Assert.Empty(services); Assert.Equal(0, a.Calls); Assert.Equal(0, b.Calls);
        }
    }

    [Fact]
    public void Competing_entity_ownership_is_rejected()
    {
        var contract = new ValueContract();
        var a = new Composition { Observations = [contract] };
        var b = new Composition { Observations = [contract] };
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceStorageComposition([a, b]).Validate([]));
        Assert.Equal("services.storage.ambiguousRepository", Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Fact]
    public async Task Preparation_failure_is_retained_without_retry_or_partial_routes()
    {
        var query = Query("first"); var failure = new InvalidOperationException("native failure");
        var storage = new Composition(query.CompilationRequest) { Failure = failure };
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveServices(Activate(new(query), new(query), storage, storage));
        await using var app = builder.Build();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => app.MapServicesAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => app.MapServicesAsync()));
        Assert.Equal(1, storage.Calls);
        Assert.Empty(((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources);
    }

    [Fact]
    public void Duplicate_endpoint_ids_fail_before_registering_or_preparing_resources()
    {
        var query = Query("first"); var storage = new Composition(query.CompilationRequest);
        var declarations = new ServiceCollection().AddSingleton(new App { First = new First(query), Second = new Duplicate(query), StoreA = new Store(storage), StoreB = new Store(storage) }).AddSingleton(new First(query))
            .AddSingleton(new Duplicate(query)).AddSingleton(new Store(storage));
        var model = new ApplicationModelBuilder(declarations).Build().Get<App>();
        var services = new ServiceCollection();
        var error = Assert.Throws<ServiceBindingValidationException>(() => services.AddCohesiveServices(model));
        Assert.Equal("services.api.duplicateEndpoint", Assert.Single(error.Validation.Diagnostics).Code);
        Assert.Empty(services); Assert.Equal(0, storage.Calls);
    }

    [Fact]
    public void Process_and_entity_endpoints_contribute_their_actual_repository_requirements()
    {
        var model = TestModels.Fulfillment();
        var required = model.Endpoints.Plan.RepositoryRequirements;
        Assert.Contains(ValueContract.FromShape(model.Domain.Orders.Definition.Shape), required);
        Assert.Contains(ValueContract.FromShape(model.Domain.Inventory.Definition.Shape), required);
        Assert.Contains(ValueContract.FromShape(model.Domain.Reservations.Definition.Shape), required);
        Assert.Equal(3, required.Count);
    }

    [Fact]
    public void Missing_entity_binding_fails_before_native_preparation()
    {
        var query = Query("first");
        var endpoint = new First(query);
        endpoint.Plan.RequireRepositories([new ValueContract()]);
        var storage = new Composition(query.CompilationRequest);
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceCollection()
            .AddCohesiveServices(Activate(endpoint, new(query), storage, storage)));
        Assert.Equal("services.storage.repositoryMissing", Assert.Single(error.Validation.Diagnostics).Code);
        Assert.Equal(0, storage.Calls);
    }

    [Fact]
    public async Task Unmapped_application_fails_host_start_without_preparing_storage()
    {
        var query = Query("first"); var storage = new Composition(query.CompilationRequest);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveServices(Activate(new(query), new(query), storage, storage));
        await using var app = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Equal(0, storage.Calls);
    }

    [Fact]
    public async Task Canceled_initial_preparation_is_retained_without_IO()
    {
        var query = Query("first"); var storage = new Composition(query.CompilationRequest);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveServices(Activate(new(query), new(query), storage, storage));
        await using var app = builder.Build();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.MapServicesAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.MapServicesAsync());
        Assert.Equal(0, storage.Calls);
    }

    [Fact]
    public void Application_and_explicit_registrations_cannot_overlap()
    {
        var query = Query("first"); var storage = new Composition(query.CompilationRequest);
        var model = Activate(new(query), new(query), storage, storage);
        var services = new ServiceCollection().AddCohesiveServices(model);
        Assert.Throws<InvalidOperationException>(() => services.AddCohesiveServices(model));
        Assert.Throws<InvalidOperationException>(() => services.AddCohesiveServices(new First(query), new Store(storage)));
    }

    [Fact]
    public async Task Explicit_overload_shares_one_storage_composition_between_models()
    {
        var query = Query("shared"); var composition = new Composition(query.CompilationRequest);
        var storage = new Store(composition);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveServices(new First(query), storage);
        builder.Services.AddCohesiveServices(new Second(query), storage);
        await using var app = builder.Build();
        await app.MapServicesAsync<First>(); await app.MapServicesAsync<Second>();
        Assert.Equal(1, composition.Calls);
        await Assert.ThrowsAsync<ServiceMappingException>(() => app.MapServicesAsync<First>());
    }
    [Fact]
    public void Explicit_registration_rejects_missing_and_competing_placement_before_IO()
    {
        var query = Query("first"); var empty = new Composition();
        var services = new ServiceCollection();
        Assert.Throws<ServiceBindingValidationException>(() => services.AddCohesiveServices(new First(query), new Store(empty)));
        Assert.Empty(services); Assert.Equal(0, empty.Calls);
        var first = new Composition(query.CompilationRequest); var second = new Composition(query.CompilationRequest);
        services.AddCohesiveServices(new First(query), new StoreA(first));
        var count = services.Count;
        var error = Assert.Throws<ServiceBindingValidationException>(() => services.AddCohesiveServices(new Second(query), new StoreB(second)));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.storage.ambiguousQuery"
            && item.Message.Contains(nameof(StoreA)) && item.Message.Contains(nameof(StoreB)));
        Assert.Equal(count, services.Count); Assert.Equal(0, first.Calls); Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Canceling_one_explicit_waiter_does_not_cancel_shared_preparation()
    {
        var query = Query("first");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var composition = new Composition(query.CompilationRequest) { Wait = completion.Task };
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveServices(new First(query), new Store(composition));
        builder.Services.AddCohesiveServices(new Second(query), new Store(composition));
        await using var app = builder.Build();
        using var canceled = new CancellationTokenSource();
        var first = app.MapServicesAsync<First>(canceled.Token);
        Assert.Equal(1, composition.Calls);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = app.MapServicesAsync<Second>();
        completion.SetResult();
        await second;
        Assert.Equal(1, composition.Calls);
    }

    [Fact]
    public async Task Root_member_order_controls_preparation_not_resolution_order()
    {
        var query = Query("first"); var order = new List<string>();
        var a = new Composition(query.CompilationRequest) { OnPrepare = () => order.Add("a") };
        var b = new Composition { OnPrepare = () => order.Add("b") };
        var root = new App { First = new First(query), Second = new Second(query), StoreA = new StoreA(a), StoreB = new StoreB(b) };
        var model = new ApplicationModelBuilder(new ServiceCollection().AddSingleton((StoreB)root.StoreB).AddSingleton((StoreA)root.StoreA)
            .AddSingleton(root)).Build().Get<App>();
        var builder = WebApplication.CreateBuilder(); builder.Services.AddCohesiveServices(model);
        await using var app = builder.Build(); await app.MapServicesAsync();
        Assert.Equal(new[] { "a", "b" }, order);
    }

    [Fact]
    public void Same_query_revision_with_different_canonical_inputs_is_rejected_before_IO()
    {
        var first = Query("same"); var changed = Query("same", changed: true);
        var storage = new Composition(first.CompilationRequest);
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceCollection()
            .AddCohesiveServices(Activate(new(first), new(changed), storage, storage)));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.storage.querySelectionMismatch"
            && item.Message.Contains("same@1"));
        Assert.Equal(0, storage.Calls);
    }

}
