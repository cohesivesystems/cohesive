using Cohesive.Host.Composition;
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
    public void Domain_owns_transition_authorities_used_by_the_separate_process()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        var dependencies = model.Process.Dependencies;
        var expected = new[]
        {
            Cohesive.Processes.Authoring.ProcessDependency.Transition(model.Domain.OrderTransitions.Submit),
            Cohesive.Processes.Authoring.ProcessDependency.Transition(model.Domain.InventoryTransitions.Reserve),
            Cohesive.Processes.Authoring.ProcessDependency.Transition(model.Domain.InventoryTransitions.Release),
            Cohesive.Processes.Authoring.ProcessDependency.Transition(model.Domain.ReservationTransitions.Create)
        };
        foreach (var transition in expected)
        {
            var dependency = Assert.Single(dependencies, candidate => candidate.Reference == transition.Reference);
            Assert.True(dependency.HasSameAuthority(transition));
        }
        Assert.Same(model.Process, model.Composition.Get<FulfillmentProcess>());
        Assert.Throws<KeyNotFoundException>(() => model.Composition.Get<OrderTransitions>());
        Assert.Throws<KeyNotFoundException>(() => model.Composition.Get<InventoryTransitions>());
        var independent = ModelActivator.Create<FulfillmentApplication>();
        Assert.NotSame(model.Domain.OrderTransitions.Submit, independent.Domain.OrderTransitions.Submit);
        Assert.Equal(model.Domain.OrderTransitions.Submit.Reference, independent.Domain.OrderTransitions.Submit.Reference);
    }

    [Fact]
    public void Colocated_and_separate_service_authoring_produce_identical_contracts()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        var process = model.Process;
        var separate = Service.Define(new("fulfillment"), new("3"), process.Provenance)
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
        var model = ModelActivator.Create<FulfillmentApplication>();
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
        var appHost = ModelActivator.Create<FulfillmentApplication>();
        var worker = ModelActivator.Create<FulfillmentApplication>();
        Assert.NotSame(appHost.Domain, worker.Domain);
        Assert.Same(appHost.Storage.Bindings, Assert.Single(appHost.Storage.Definition));
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
        var process = new FulfillmentProcess(domain);
        var definitions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => process.Definition)));
        Assert.All(definitions, definition => Assert.Same(definitions[0], definition));
        Assert.True(definitions[0].IsValid);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            if (!ReferenceEquals(definitions[0], process.Definition)) throw new InvalidOperationException();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
