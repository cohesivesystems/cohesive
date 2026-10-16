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
        query => query.FromRoute<string>("id", value => value).OkOrNotFound());
    static void Second(ServiceApiImplementation<Catalog> bindings) => bindings.Bind(api => api.Second, Query("second"), new("1"), 10, 1000,
        query => query.FromRoute<string>("id", value => value).OkOrNotFound());

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
            bindings.Bind(_ => other.First, Query("first"), new("1"), 10, 1000, _ => { })));
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
            bindings.Bind(a => a.Second, Query("second"), new("1"), 10, 1000, _ => { }))
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
            binding => binding.FromRoute<string>("id", value => value).OkOrNotFound());
        var builder = new ServiceApiBindingsBuilder().Implement(api, Contribute);
        var error = Assert.Throws<ServiceBindingValidationException>(() => builder.Implement(api, Contribute));
        Assert.Contains(error.Validation.Diagnostics, item => item.Code == "services.api.duplicateImplementation");
    }

    [Fact]
    public void Competing_query_preparation_bounds_are_rejected()
    {
        var api = new Catalog();
        var query = Query("shared");
        var model = new ServiceApiBindingsBuilder().Implement(api, bindings => bindings.Bind(a => a.First, query, new("1"), 10, 1000, _ => { }));
        Assert.Throws<ServiceBindingValidationException>(() => model.Implement(api, bindings => bindings.Bind(a => a.Second, query, new("1"), 11, 1000, _ => { })));
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
            bindings.Bind(a => a.First, Query("discarded"), new("1"), 10, 1000, _ => { });
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
