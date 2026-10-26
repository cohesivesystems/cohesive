using System.Collections.Immutable;
using Cohesive.Api.Execution.Services;
using Cohesive.Storage;

namespace Cohesive.Tests.Api;

public sealed class ServiceHistoryAdmissionTests
{
    sealed record History(StorageHistoryDeclaration HistoryDeclaration) : IStorageHistoryBinding;
    sealed class Source(EntityStateAuthority authority) : IServiceStorageComposition
    {
        public ImmutableArray<object> Bindings { get; } = [new History(new("orders", authority, EntityReconstitutionMode.FullReplay))];
        public ImmutableArray<ValueContract> Observations => [];
        public ImmutableArray<QualifiedShapeId> Shapes => [];
        public int Preparations { get; private set; }
        public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> queries) { Preparations++; return this; }
        public ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No IO in admission");
    }

    [Fact]
    public void Conflicting_resource_history_is_rejected_before_preparation()
    {
        var state = new Source(EntityStateAuthority.State);
        var events = new Source(EntityStateAuthority.Events);
        var composition = new ServiceStorageComposition([("StateModel", state), ("EventsModel", events)]);
        var error = Assert.Throws<ServiceBindingValidationException>(() => composition.Validate([]));
        var diagnostic = Assert.Single(error.Validation.Diagnostics);
        Assert.Equal("services.storage.historyConflict", diagnostic.Code);
        Assert.Contains("orders", diagnostic.Message);
        Assert.Contains("StateModel", diagnostic.Message);
        Assert.Contains("EventsModel", diagnostic.Message);
        Assert.Equal(0, state.Preparations + events.Preparations);
        Assert.Throws<InvalidOperationException>(() => composition.Explanation);
    }

    [Fact]
    public void Equivalent_resource_history_can_be_shared()
    {
        var composition = new ServiceStorageComposition([new Source(EntityStateAuthority.State), new Source(EntityStateAuthority.State)]);
        composition.Validate([]);
        Assert.NotNull(composition.Explanation);
    }
}
