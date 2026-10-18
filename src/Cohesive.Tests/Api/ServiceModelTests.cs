using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
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
        query => query.FromRoute<string>("id", value => value).OkOrNotFound(), RelationQueryConsistency.SingleSnapshot);
    static void Second(ServiceApiImplementation<Catalog> bindings) => bindings.Bind(api => api.Second, Query("second"), new("1"), 10, 1000,
        query => query.FromRoute<string>("id", value => value).OkOrNotFound(), RelationQueryConsistency.SingleSnapshot);

    [Fact]
    public async Task Partial_contributions_map_one_surface_and_prepare_once_before_requests()
    {
        var api = new Catalog();
        var builder = WebApplication.CreateBuilder();
        var persistence = new Persistence();
        var model = new ServiceApiBindingsBuilder().Implement(api, Second).Implement(api, First);
        Assert.Equal(2, model.Plan.QueryDependencies.Queries.Length);
        var prepared = model.Build(builder.Services, persistence);
        Assert.Equal(2, persistence.Prepared);
        await using var app = builder.Build();
        app.MapServices(prepared);
        var mappingError = Assert.Throws<ServiceMappingException>(() => app.MapServices(prepared));
        var diagnostic = Assert.Single(mappingError.Validation.Diagnostics);
        Assert.Equal("services.api.alreadyMapped", diagnostic.Code);
        Assert.Equal("/api/mapping/state", diagnostic.Location);
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
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceApiBindingsBuilder()
            .Implement(api, First).Build(new ServiceCollection(), new Persistence()));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.missingImplementation" && item.Message.Contains(api.Second.Id.Value));
    }

    [Fact]
    public void Selectors_cannot_capture_another_api_instance()
    {
        var api = new Catalog(); var other = new Catalog();
        Assert.Throws<ServiceBindingValidationException>(() => new ServiceApiBindingsBuilder().Implement(api, bindings =>
            bindings.Bind(_ => other.First, Query("first"), new("1"), 10, 1000, _ => { }, RelationQueryConsistency.SingleSnapshot)));
    }

    [Fact]
    public async Task Duplicate_routes_fail_without_publishing_partial_routes()
    {
        var api = new Catalog(duplicateRoute: true);
        var builder = WebApplication.CreateBuilder();
        var prepared = new ServiceApiBindingsBuilder().Implement(api, First).Implement(api, Second).Build(builder.Services, new Persistence());
        await using var app = builder.Build();
        var error = Assert.Throws<ServiceMappingException>(() => app.MapServices(prepared));
        Assert.Equal("/api/routes/GET/~1first~1{id}", Assert.Single(error.Validation.Diagnostics).Location);
        Assert.All(error.Validation.Diagnostics, item => Assert.NotEqual("/api/mapping", item.Location));
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources);
    }

    [Fact]
    public async Task Incomplete_query_binding_publishes_no_routes()
    {
        var api = new Catalog();
        var builder = WebApplication.CreateBuilder();
        var prepared = new ServiceApiBindingsBuilder().Implement(api, First).Implement(api, bindings =>
            bindings.Bind(a => a.Second, Query("second"), new("1"), 10, 1000, _ => { }, RelationQueryConsistency.SingleSnapshot))
            .Build(builder.Services, new Persistence());
        await using var app = builder.Build();
        var error = Assert.Throws<ServiceMappingException>(() => app.MapServices(prepared));
        Assert.Contains(error.Validation.Diagnostics, item => item.Message.Contains(api.Second.Id.Value) && item.Location == "/api/endpoints/" + api.Second.Id.Value.Replace("~", "~0").Replace("/", "~1"));
        Assert.All(error.Validation.Diagnostics, item => Assert.NotEqual("/api/mapping", item.Location));
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources);
    }

    [Fact]
    public void Separately_declared_catalog_and_late_contribution_rules_are_supported()
    {
        var api = new Catalog();
        var builder = new ServiceApiBindingsBuilder().Implement(api, api.Definition, First).Implement(api, api.Definition, Second);
        _ = builder.Build(new ServiceCollection(), new Persistence());
        Assert.Throws<InvalidOperationException>(() => builder.Implement(api, First));
    }

    [Fact]
    public void Competing_partial_implementations_fail_before_routes_are_published()
    {
        var api = new Catalog();
        var query = Query("first");
        void Contribute(ServiceApiImplementation<Catalog> bindings) => bindings.Bind(a => a.First, query, new("1"), 10, 1000,
            binding => binding.FromRoute<string>("id", value => value).OkOrNotFound(), RelationQueryConsistency.SingleSnapshot);
        var builder = new ServiceApiBindingsBuilder().Implement(api, Contribute);
        var error = Assert.Throws<ServiceBindingValidationException>(() => builder.Implement(api, Contribute));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.duplicateImplementation");
    }

    [Fact]
    public void Competing_query_preparation_bounds_are_rejected()
    {
        var api = new Catalog();
        var query = Query("shared");
        var model = new ServiceApiBindingsBuilder().Implement(api, bindings => bindings.Bind(a => a.First, query, new("1"), 10, 1000, _ => { }, RelationQueryConsistency.SingleSnapshot));
        Assert.Throws<ServiceBindingValidationException>(() => model.Implement(api, bindings => bindings.Bind(a => a.Second, query, new("1"), 11, 1000, _ => { }, RelationQueryConsistency.SingleSnapshot)));
    }

    [Fact]
    public void Coverage_is_checked_without_preparing_persistence()
    {
        var model = new ServiceApiBindingsBuilder().Implement(new Catalog(), First);
        var persistence = new Persistence();
        Assert.Throws<ServiceBindingValidationException>(() => model.Plan.Definition);
        Assert.Throws<ServiceBindingValidationException>(() => model.Build(new ServiceCollection(), persistence));
        Assert.Equal(0, persistence.Prepared);
    }

    [Fact]
    public void Repeated_build_returns_same_preparation_and_does_not_register_more_services()
    {
        var model = new ServiceApiBindingsBuilder().Implement(new Catalog(), bindings => { First(bindings); Second(bindings); });
        var services = new ServiceCollection();
        var persistence = new Persistence();
        var first = model.Build(services, persistence);
        var count = services.Count;
        Assert.Same(first, model.Build(services, persistence));
        Assert.Equal(2, persistence.Prepared);
        Assert.Equal(count, services.Count);
        var error = Assert.Throws<ServiceBindingValidationException>(() => model.Build(new ServiceCollection(), persistence));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.hostMismatch");
    }

    [Fact]
    public void Colocated_pair_uses_canonical_service_and_keeps_response_timing_separate()
    {
        var process = new EchoProcess();
        var owner = Cohesive.Api.Api.Define();
        var response = ServiceProcessResponse.WaitForResult(TimeSpan.FromSeconds(1));
        ServiceBuilder Configure(ServiceBuilder service) => service.Operation("start").Run(process.Definition)
            .ReturnAfterAdmission(ProcessExecutionLifetime.Host).Operation("result").ReadResultOf(process.Definition);
        var pair = owner.Process(process).Accepted<Detail>(new("colocated"), new("3"), Configure,
            "start", "result", "/echo", "/echo/{instanceId}", response);
        var separate = Configure(Service.Define(new("colocated"), new("3"), process.Provenance))
            .AcceptedApi<Detail, string>("start", "result", "/echo", "/echo/{instanceId}", response);
        Assert.Equal(separate.Declaration.Reference, pair.Declaration.Reference);
        Assert.Same(response, pair.Response);
        Assert.Same(process, pair.Process);
        Assert.Equal(2, owner.Build().Endpoints.Count);
        Assert.Same(pair.Start, owner.Build().Endpoints[0]);
    }

    [Fact]
    public void Invalid_colocated_service_does_not_publish_either_endpoint()
    {
        var process = new EchoProcess();
        var owner = Cohesive.Api.Api.Define();
        Assert.Throws<ServiceBindingValidationException>(() => owner.Process(process).Accepted<Detail>(
            new("invalid"), new("1"), service => service.Operation("start").Run(process.Definition)
                .ReturnAfterAdmission(ProcessExecutionLifetime.Host),
            "start", "missing-result", "/echo", "/echo/{instanceId}"));
        Assert.Empty(owner.Build().Endpoints);
    }

    [Fact]
    public void Colocated_pair_rejects_another_process_before_registering_routes()
    {
        var selected = new EchoProcess();
        var foreign = new EchoProcess("foreign");
        var owner = Cohesive.Api.Api.Define();
        var error = Assert.Throws<ServiceBindingValidationException>(() => owner.Process(selected).Accepted<Detail>(
            new("invalid"), new("1"), service => service.Operation("start").Run(foreign.Definition)
                .ReturnAfterAdmission(ProcessExecutionLifetime.Host).Operation("result").ReadResultOf(foreign.Definition),
            "start", "result", "/echo", "/echo/{instanceId}"));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.processMismatch");
        Assert.Empty(owner.Build().Endpoints);
    }

    sealed class StandardProcessApi : ApiModel
    {
        public StandardProcessApi(string id)
        {
            var process = new EchoProcess();
            Pair = Endpoints.Process(process).Accepted<ServiceProcessCommand<string>>(new(id), new("1"),
                service => service.Operation("start").Require(new("shared")).Run(process.Definition)
                    .ReturnAfterAdmission(ProcessExecutionLifetime.Host)
                    .Operation("result").Require(new("shared")).ReadResultOf(process.Definition),
                "start", "result", "/" + id, "/" + id + "/{instanceId}");
        }
        public ColocatedProcessApi<ServiceProcessCommand<string>, string, string> Pair { get; }
    }
    sealed class SelectedAuthorization(string allowedOperation) : IServiceInvocationAuthorization
    {
        public ValueTask<Cohesive.Identity.ScopeRef?> AdmitAsync(OperationContext context, Cohesive.Api.Services.ServiceOperation operation) =>
            ValueTask.FromResult<Cohesive.Identity.ScopeRef?>(operation.Id == allowedOperation ? new("tenant", "tenant", PartitionKey: "tenant") : null);
        public ValueTask<bool> AuthorizeResourceAsync(OperationContext context, Cohesive.Api.Services.ServiceOperation operation,
            EntitySnapshot snapshot) => ValueTask.FromResult(false);
    }

    [Fact]
    public async Task Default_http_policies_keep_operations_and_services_isolated_despite_shared_requirement_ids()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        var model = new ServiceApiBindingsBuilder()
            .Implement(new StandardProcessApi("first"), b => b.BindHosted(api => api.Pair, "test", new SelectedAuthorization("start"), 2))
            .Implement(new StandardProcessApi("second"), b => b.BindHosted(api => api.Pair, "test", new SelectedAuthorization("result"), 2));
        var prepared = model.Build(builder.Services, new Persistence());
        await using var app = builder.Build();
        app.MapServices(prepared);
        var authorization = app.Services.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = app.Services };
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).Cast<RouteEndpoint>().ToArray();
        Assert.Equal(4, endpoints.Length);
        var names = new HashSet<string>();
        foreach (var endpoint in endpoints)
        {
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()).Policy!;
            Assert.True(names.Add(policy));
            var result = await authorization.AuthorizeAsync(new System.Security.Claims.ClaimsPrincipal(), http, policy);
            Assert.Equal(endpoint.RoutePattern.RawText is "/first" or "/second/{instanceId}", result.Succeeded);
        }
    }

    sealed class CompleteBindings : ApiBindingModel
    {
        readonly Catalog api;
        public CompleteBindings(Catalog api)
        {
            this.api = api;
            Bindings.Implement(api, bindings => { First(bindings); Second(bindings); });
        }
        public void AddLate() => Bindings.Implement(api, _ => { });
    }

    [Fact]
    public void Api_binding_model_freezes_offline_recipes_before_native_preparation()
    {
        var model = new CompleteBindings(new Catalog());
        ((Cohesive.Prelude.IAuthoringModel)model).Complete();
        Assert.NotNull(model.Plan.Definition);
        Assert.Throws<InvalidOperationException>(model.AddLate);
        var persistence = new Persistence();
        var services = new ServiceCollection();
        var prepared = model.Build(services, persistence);
        Assert.Same(prepared, model.Build(services, persistence));
        Assert.Equal(2, persistence.Prepared);
    }

    sealed class EchoProcess(string id = "echo") : ProcessModel<string, string>
    {
        protected override ProcessAuthoringMetadata Metadata => new(new(id), new("1"), new("return"),
            ProcessRecoveryPolicy.ContinueAttempt, new(new("tests"), new("tests/echo"), DocumentOrigin.Generated));
        protected override Process<string, string> CreateDefinition() => ProcessAuthoring.Create<string, string>(Metadata,
            builder => builder.Return(new("return"), builder.Input.Value));
        protected override ProcessTask<string> Execute(ProcessContext process, string input) => throw new NotSupportedException();
    }
    sealed class ProcessApi : ApiModel
    {
        public ProcessApi(ServiceBuilder service, EchoProcess process) => Echo = Endpoints.Process(service, "echo", process).Route("POST", "/echo");
        public ServiceProcessEndpoint<string, string> Echo { get; }
    }
    static ServiceBuilder EchoService(EchoProcess process, string id = "echo-service") =>
        Service.Define(new(id), new("1"), process.Provenance).Operation("echo").Run(process.Definition).ExecuteEphemerally(TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Process_binding_and_logging_build_without_a_database()
    {
        var process = new EchoProcess(); var service = EchoService(process); var api = new ProcessApi(service, process);
        var builder = WebApplication.CreateBuilder();
        var persistence = new Persistence();
        var model = new ServiceApiBindingsBuilder().Implement(api, bindings => bindings.Bind(a => a.Echo, service, process,
            "test", new IdentityServiceInvocationAuthorization("tenant", new("Tenant")), logPrivateFailures: true));
        Assert.Single(model.Plan.Definition.Endpoints);
        var prepared = model.Build(builder.Services, persistence);
        var count = builder.Services.Count;
        Assert.Same(prepared, model.Build(builder.Services, persistence));
        Assert.Equal(count, builder.Services.Count);
        Assert.Equal(0, persistence.Prepared);
        await using var app = builder.Build();
        app.MapServices(prepared);
        Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
    }

    [Fact]
    public void Process_mismatch_is_rejected_during_implementation_without_persistence()
    {
        var process = new EchoProcess(); var api = new ProcessApi(EchoService(process), process);
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceApiBindingsBuilder().Implement(api,
            bindings => bindings.Bind(a => a.Echo, EchoService(process, "another-service"), process,
                "test", new IdentityServiceInvocationAuthorization("tenant", new("Tenant")))));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.processMismatch");
    }

    [Fact]
    public void Base_http_attachment_preserves_process_proof_and_operation_identity()
    {
        var process = new EchoProcess(); var api = new ProcessApi(EchoService(process), process);
        ApiEndpoint untyped = api.Echo;
        ApiEndpoint<string, string> typed = api.Echo;
        foreach (var attached in new[] { untyped.WithHttp(new("POST", "/base", [], new(typeof(string)))),
            typed.WithHttp(new("POST", "/typed", [], new(typeof(string)))) })
        {
            var proof = Assert.IsType<ServiceProcessEndpoint<string, string>>(attached);
            Assert.Equal(api.Echo.Service, proof.Service);
            Assert.Equal(api.Echo.Process, proof.Process);
            Assert.Equal("echo", proof.OperationId);
        }
    }

    [Fact]
    public void Finite_endpoint_authoring_rejects_multiple_operations_for_same_process()
    {
        var process = new EchoProcess();
        var service = EchoService(process).Operation("other").Run(process.Definition).ExecuteEphemerally(TimeSpan.FromSeconds(2));
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ProcessApi(service, process));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.operationMismatch");
    }

    [Fact]
    public void Failed_contribution_rolls_back_queries_endpoints_and_host_recipes()
    {
        var api = new Catalog();
        var model = new ServiceApiBindingsBuilder().Implement(api, First);
        Assert.Throws<ServiceBindingValidationException>(() => model.Implement(api, bindings =>
        {
            Second(bindings);
            bindings.Bind(a => a.First, Query("discarded"), new("1"), 10, 1000, _ => { }, RelationQueryConsistency.SingleSnapshot);
        }));
        model.Implement(api, Second);
        Assert.Equal(2, model.Plan.QueryDependencies.Queries.Length);
        var persistence = new Persistence();
        model.Build(new ServiceCollection(), persistence);
        Assert.Equal(2, persistence.Prepared);
    }

    [Fact]
    public void Document_reference_projects_metadata_without_changing_wire_contract()
    {
        var document = new EchoProcess().Definition.Document;
        Assert.Equal(document.Metadata.DefinitionId, document.Reference.DefinitionId);
        Assert.Equal(document.Metadata.RevisionId, document.Reference.RevisionId);
        Assert.Equal(document.Metadata.Fingerprint, document.Reference.Fingerprint);
        using var serialized = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(document));
        Assert.False(serialized.RootElement.TryGetProperty("Reference", out _));
    }

    [Fact]
    public void Endpoint_requirement_rejects_a_weaker_custom_resolver_before_mapping()
    {
        var api = new Catalog();
        var bindings = new ServiceApiBindingsBuilder().Implement(api, First).Implement(api, Second);
        var resolver = new Persistence(RelationQueryConsistency.IndependentSources);
        var error = Assert.Throws<ServiceBindingValidationException>(() => bindings.Build(new ServiceCollection(), resolver));
        Assert.Contains(error.Validation.Diagnostics, diagnostic => diagnostic.Code == ServiceBindingDiagnosticCodes.QueryPlacementUnsupported
            && diagnostic.Location == "/bindings/queries/consistency");
    }

    [Fact]
    public void Cached_reader_is_rechecked_for_a_stronger_requirement_without_repreparing()
    {
        var query = Query("cached");
        var preparations = 0;
        var persistence = new ServicePersistenceBuilder().Query(query, (_, _) =>
        { preparations++; return new Reader(query, RelationQueryConsistency.IndependentSources); }).Build();
        var reader = persistence.Query(query, 10, 1000, RelationQueryConsistency.IndependentSources);
        Assert.Throws<ServiceBindingValidationException>(() => persistence.Query(query, 10, 1000, RelationQueryConsistency.SingleSnapshot));
        Assert.Same(reader, persistence.Query(query, 10, 1000, RelationQueryConsistency.IndependentSources));
        Assert.Equal(1, preparations);
    }

    [Theory]
    [InlineData(RelationQueryConsistency.SingleSnapshot)]
    [InlineData(RelationQueryConsistency.IndependentSources)]
    public void Native_snapshot_satisfies_either_declared_requirement(RelationQueryConsistency required)
    {
        var reader = new Reader(Query("native"));
        Assert.Same(reader, ServiceQueryConsistencyValidation.Require(reader, required));
    }

    [Fact]
    public void Unknown_reader_guarantee_is_not_admitted()
    {
        var reader = new Reader(Query("unknown"), (RelationQueryConsistency)99);
        Assert.Throws<ServiceBindingValidationException>(() => ServiceQueryConsistencyValidation.Require(reader, RelationQueryConsistency.IndependentSources));
    }

    [Fact]
    public void Same_query_cannot_have_competing_consistency_requirements()
    {
        var query = Query("same");
        var plan = new ServiceImplementationPlan();
        plan.Query(query, new("1"), 10, 1000, RelationQueryConsistency.SingleSnapshot);
        var error = Assert.Throws<ServiceBindingValidationException>(() => plan.Query(query, new("1"), 10, 1000, RelationQueryConsistency.IndependentSources));
        Assert.Contains(error.Validation.Diagnostics, diagnostic => diagnostic.Code == "services.api.querySelectionMismatch");
    }

    sealed class QueryBindings : ApiBindingModel
    {
        public QueryBindings() { var api = new Catalog(); Bindings.Implement(api, First).Implement(api, Second); }
    }
    sealed class OwnedResource : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public async Task DI_prepares_once_from_shared_resources_before_mapping_and_disposes_only_at_host_end()
    {
        var builder = WebApplication.CreateBuilder();
        var created = 0;
        var resource = new OwnedResource();
        builder.Services.AddKeyedSingleton<OwnedResource>("existing-resource", (_, _) => { created++; return resource; });
        var preparations = 0;
        var persistence = new Persistence();
        builder.Services.AddCohesiveServices(new QueryBindings(), (services, token) =>
        {
            preparations++;
            Assert.Same(resource, services.GetRequiredKeyedService<OwnedResource>("existing-resource"));
            return ValueTask.FromResult<IServicePersistence>(persistence);
        });
        Assert.Equal(0, created);
        var app = builder.Build();
        try
        {
            await app.MapServicesAsync<QueryBindings>();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            Assert.Equal("one", (await client.GetFromJsonAsync<Detail>("/first/one"))!.Id);
            Assert.Equal(1, preparations);
            Assert.Equal(1, created);
            Assert.Equal(2, persistence.Prepared);
            Assert.Equal(0, resource.Disposals);
            await app.StopAsync();
        }
        finally { await app.DisposeAsync(); }
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task Failed_DI_preparation_is_retained_and_borrowed_resources_are_not_disposed()
    {
        var builder = WebApplication.CreateBuilder();
        var resource = new OwnedResource();
        builder.Services.AddKeyedSingleton("existing-resource", resource);
        var attempts = 0;
        builder.Services.AddCohesiveServices(new QueryBindings(), (services, token) =>
        {
            attempts++;
            Assert.Same(resource, services.GetRequiredKeyedService<OwnedResource>("existing-resource"));
            throw new InvalidOperationException("schema requires migration");
        });
        await using (var app = builder.Build())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.MapServicesAsync<QueryBindings>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.MapServicesAsync<QueryBindings>());
            Assert.Empty(((IEndpointRouteBuilder)app).DataSources);
        }
        Assert.Equal(1, attempts);
        Assert.Equal(0, resource.Disposals);
        resource.Dispose();
    }

    [Fact]
    public async Task Canceled_initial_preparation_does_not_allocate_or_publish_routes()
    {
        var builder = WebApplication.CreateBuilder();
        var attempts = 0;
        builder.Services.AddCohesiveServices(new QueryBindings(), (_, _) =>
        {
            attempts++;
            return ValueTask.FromResult<IServicePersistence>(new Persistence());
        });
        await using var app = builder.Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.MapServicesAsync<QueryBindings>(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.MapServicesAsync<QueryBindings>());
        Assert.Equal(0, attempts);
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources);
    }

    [Fact]
    public void Duplicate_DI_model_registration_fails_before_resource_preparation()
    {
        var services = new ServiceCollection();
        services.AddCohesiveServices(new QueryBindings(), (_, _) => ValueTask.FromResult<IServicePersistence>(new Persistence()));
        Assert.Throws<InvalidOperationException>(() => services.AddCohesiveServices(new QueryBindings(),
            (_, _) => ValueTask.FromResult<IServicePersistence>(new Persistence())));
    }

    sealed class Persistence(RelationQueryConsistency suppliedConsistency = RelationQueryConsistency.SingleSnapshot) : IServicePersistence
    {
        internal int Prepared;
        public IEntityRepository Repository(ValueContract observation) => throw new InvalidOperationException("Query composition must not resolve an entity repository.");
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query, int maximumRows, long maximumBytes, RelationQueryConsistency consistency)
        {
            Prepared++;
            return (IRelationQueryReader<TInput, TResult>)(object)new Reader((RelationQuery<string, Detail?>)(object)query, suppliedConsistency);
        }
    }
    sealed class Reader(RelationQuery<string, Detail?> definition, RelationQueryConsistency consistency = RelationQueryConsistency.SingleSnapshot) : IRelationQueryReader<string, Detail?>
    {
        /// <inheritdoc />
        public RelationQueryConsistency Consistency => consistency;
        public RelationQuery<string, Detail?> Definition => definition;
        public Task<Detail?> ReadAsync(string input, CancellationToken cancellationToken = default) => Task.FromResult<Detail?>(new(input));
    }
}
