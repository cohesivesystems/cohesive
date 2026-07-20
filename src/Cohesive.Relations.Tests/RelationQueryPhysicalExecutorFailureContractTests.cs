using Cohesive.Relations.Acquisition;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryPhysicalExecutorFailureContractTests
{
    [Fact]
    public async Task ExecuteAsync_SourceReaderExceptionPropagatesAndStopsBeforeRelatedReads()
    {
        var compilation = FederatedLoadPhysicalExecutionFixture.Create(
            FederatedLoadRelationFixture.QueryDocument,
            maximumBatchSize: 2);
        var providerFailure = new InvalidOperationException("The source provider failed.");
        var loads = Reader(
            compilation,
            FederatedLoadPhysicalExecutionFixture.LoadsSource,
            _ => throw providerFailure);
        var customers = Reader(
            compilation,
            FederatedLoadPhysicalExecutionFixture.CustomersSource,
            static _ => new(RelationQuerySourceReadState.Complete));
        var equipment = Reader(
            compilation,
            FederatedLoadPhysicalExecutionFixture.EquipmentSource,
            static _ => new(RelationQuerySourceReadState.Complete));
        var request = new RelationQueryPhysicalExecutionRequest(
            compilation.Plan,
            compilation.PhysicalPlan,
            compilation.Realization,
            new("tests/source-reader-exception"),
            capabilities: FederatedLoadPhysicalExecutionFixture.AvailableCapabilities(compilation.Plan));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RelationQueryPhysicalExecutor([loads, customers, equipment])
                .ExecuteAsync(request)
                .AsTask());

        Assert.Same(providerFailure, exception);
        Assert.Single(loads.Requests);
        Assert.Empty(customers.Requests);
        Assert.Empty(equipment.Requests);
    }

    static DeterministicRelationQuerySourceReader Reader(
        FederatedLoadPhysicalExecutionFixture.Compilation compilation,
        Cohesive.Relations.Physical.RelationQuerySourceInstanceId sourceId,
        Func<RelationQuerySourceReadRequest, RelationQuerySourceReadResult> resultFactory)
    {
        var source = FederatedLoadPhysicalExecutionFixture.Source(compilation, sourceId);
        return new(
            new(source.Id, source.ExecutionDomain, source.TargetProfile),
            [],
            resultFactory);
    }
}
