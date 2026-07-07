using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;

namespace Cohesive.Tests.Model;

public sealed class EntityStatePopulateTests
{
    [Fact]
    public void Populate_MapsEntityStateIntoClrShape()
    {
        var entity = new CustomerEntity();
        var state = entity.CreateState("customer-1", new
        {
            Id = "customer-1",
            Name = "Acme Freight"
        });

        var dto = state.Populate<CustomerDto>();

        Assert.Equal("customer-1", dto.Id);
        Assert.Equal("Acme Freight", dto.Name);
    }

    [Fact]
    public void Populate_MapsEntitySnapshotIntoClrShapeWithExplicitFieldAlias()
    {
        var entity = new CustomerEntity();
        var snapshot = entity.Snapshot(entity.CreateState("customer-1", new
        {
            Id = "customer-1",
            Name = "Acme Freight"
        }));
        
        var dto = snapshot.Populate<CustomerSummary>(static builder => builder.Map(nameof(CustomerEntity.Id), summary => summary.CustomerId));

        Assert.Equal("customer-1", dto.CustomerId);
        Assert.Equal("Acme Freight", dto.Name);
    }

    sealed class CustomerEntity : Entity<CustomerEntity>
    {
        public CustomerEntity()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Name = MutableField<string>(nameof(Name));
        }

        public Field<string> Id { get; }

        public Field<string> Name { get; }
    }

    sealed record CustomerDto(string Id, string Name);

    sealed record CustomerSummary(string CustomerId, string Name);
}
