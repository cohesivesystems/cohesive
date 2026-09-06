using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Storage;

namespace Cohesive.Tests.Storage.Conformance;

public sealed class InMemoryRepositoryConformanceTests
{
    [Theory]
    [MemberData(nameof(EntityRepositoryConformance.AllCases), MemberType = typeof(EntityRepositoryConformance))]
    public Task Conforms(RepositoryProbe probe) => EntityRepositoryConformance.Verify(
        new InMemoryEntityOutboxRepository(RunControlFixture.Entity, EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant))), probe);
}
