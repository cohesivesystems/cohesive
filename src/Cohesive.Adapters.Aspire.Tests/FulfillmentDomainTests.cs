using AspireFirst.Orders;
using Cohesive.Relations.Authoring;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentDomainTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Query_uses_canonical_entity_graphs_and_compiles_two_native_joins()
    {
        var author = RelationQuery.Expression();
        var shape = FulfillmentDomain.Orders.QueryShape(author);
        Assert.Equal(FulfillmentDomain.Orders.Definition.StateShape.QualifiedId, shape.Id);
        Assert.Same(OrderStorage.Entity, FulfillmentDomain.Orders.Definition);
        Assert.Equal(3, FulfillmentDomain.Definition.Entities.Length);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var artifact = OrderDetailsQuery.Artifact;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"Query access after domain setup: {started.Elapsed.TotalMilliseconds:F1} ms, {bytes} allocated bytes; cold only in an isolated test process.");
        Assert.Same(artifact, OrderDetailsQuery.Artifact);
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
            if (!ReferenceEquals(artifact, OrderDetailsQuery.Artifact))
                throw new InvalidOperationException("The prepared query was not retained.");
        output.WriteLine($"10,000 warm artifact accesses: {GC.GetAllocatedBytesForCurrentThread() - allocated} allocated bytes.");
        Assert.Equal(2, artifact.Statement.Text.Split("LEFT JOIN", StringSplitOptions.None).Length - 1);
        Assert.Single(artifact.Parameters);
    }
}
