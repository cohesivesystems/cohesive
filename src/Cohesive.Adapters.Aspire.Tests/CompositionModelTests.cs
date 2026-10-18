using AspireFirst.Orders;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Api.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Adapters.AspNet.Services;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class CompositionModelTests
{
    [Fact]
    public void Colocated_and_separate_service_authoring_produce_identical_contracts()
    {
        var model = new FulfillmentApplication();
        var process = model.Process;
        var separate = Service.Define(new("fulfillment"), new("2"), process.Provenance)
            .Use(new ServiceDependencies().Process(process))
            .Operation("fulfill").Require(new("fulfillment.write")).Run(process.Definition)
            .ReturnAfterAdmission(ProcessExecutionLifetime.Host)
            .Operation("result").Require(new("fulfillment.read")).ReadResultOf(process.Definition)
            .AcceptedApi<ServiceProcessCommand<FulfillOrder>, FulfillmentResult>("fulfill", "result", "/fulfillment", "/fulfillment/results/{instanceId}");
        Assert.Equal(separate.Declaration.Reference, model.Api.Fulfill.Declaration.Reference);
        Assert.Equal(separate.Start.Id, model.Api.Fulfill.Start.Id);
        Assert.Equal(separate.Result.Id, model.Api.Fulfill.Result.Id);
        Assert.Same(process, model.Api.Fulfill.Process);
    }

    [Fact]
    public void Accepted_fulfillment_reuses_process_and_declares_independent_receipt_and_polling_contracts()
    {
        var model = new FulfillmentApplication();
        var api = model.Api;
        Assert.Equal(7, api.Definition.Endpoints.Count);
        Assert.Equal(api.Fulfill.Service.Build().Reference, api.Fulfill.Declaration.Reference);
        Assert.Equal(ApiResultKind.Accepted, api.Fulfill.Start.Operation.PrimaryResult.Kind);
        Assert.Equal(api.Fulfill.Start.Id, api.Fulfill.Result.Operation.ResultOf);
        Assert.Equal("/fulfillment", api.Fulfill.Start.Operation.Http!.Route);
        Assert.Equal("/fulfillment/results/{instanceId}", api.Fulfill.Result.Operation.Http!.Route);
        var service = api.Fulfill.Declaration.GetDefinition<ServiceDefinition>();
        var start = Assert.IsType<ServiceProcessOperation>(service.Operations[0]);
        var result = Assert.IsType<ServiceProcessResultOperation>(service.Operations[1]);
        Assert.Equal(model.Process.Definition.Reference, start.Process);
        Assert.Equal(start.Process, result.Process);
        Assert.Equal(ProcessExecutionLifetime.Host, start.Execution!.Lifetime);
        Assert.Equal(ServiceProcessCompletion.Admission, start.Execution.Completion);
        Assert.Equal("fulfillment.write", Assert.Single(start.AuthorizationRequirements).Id);
        Assert.Equal("fulfillment.read", Assert.Single(result.AuthorizationRequirements).Id);
        Assert.Null(api.Fulfill.Response.MaximumWait);

        // Definition ownership is inferred for both the application catalog and a standalone pair.
        foreach (var declaration in new IApiDeclaration[] { api, api.Fulfill })
        {
            var implementation = new ServiceApiBindingsBuilder().Implement(declaration, _ => { });
            var error = Assert.Throws<ServiceBindingValidationException>(() => implementation.Plan.Definition);
            Assert.Equal(declaration.Definition.Endpoints.Count, error.Validation.Diagnostics.Length);
            Assert.All(error.Validation.Diagnostics, diagnostic => Assert.Equal("services.api.missingImplementation", diagnostic.Code));
        }
    }

    [Fact]
    public void Independent_program_models_keep_typed_authorities_isolated_but_semantically_equal()
    {
        var appHost = new FulfillmentApplication();
        var worker = new FulfillmentApplication();
        Assert.NotSame(appHost.Domain, worker.Domain);
        Assert.Same(appHost.Storage.Bindings.Mapping(appHost.Domain.Inventory),
            appHost.Storage.InventoryBindings.Mapping(appHost.Domain.Inventory));
        Assert.NotSame(appHost.Domain.Orders.Definition, worker.Domain.Orders.Definition);
        Assert.Equal(appHost.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            worker.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        Assert.Equal(appHost.Process.Definition.Reference, worker.Process.Definition.Reference);
        Assert.Throws<ArgumentException>(() => appHost.Storage.Bindings.Mapping(worker.Domain.Orders));
    }

    [Fact]
    public async Task Generated_process_definition_is_shared_across_concurrent_access()
    {
        var domain = new FulfillmentDomain();
        var process = new FulfillmentProcess(domain, new(domain), new(domain));
        var definitions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => process.Definition)));
        Assert.All(definitions, definition => Assert.Same(definitions[0], definition));
        Assert.True(definitions[0].IsValid);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            if (!ReferenceEquals(definitions[0], process.Definition)) throw new InvalidOperationException();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
