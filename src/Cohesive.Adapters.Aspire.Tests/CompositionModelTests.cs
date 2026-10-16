using AspireFirst.Orders;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class CompositionModelTests
{
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
