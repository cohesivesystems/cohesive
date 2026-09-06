using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Tests.Storage.Conformance;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteRepositoryConformanceTests
{
    [Theory]
    [MemberData(nameof(EntityRepositoryConformance.AllCases), MemberType = typeof(EntityRepositoryConformance))]
    public async Task Conforms(RepositoryProbe probe)
    {
        using var file = new DatabaseFixture();
        var mapping = new SqliteEntityRepositoryMapping(RunControlFixture.Entity, nameof(RunControl.Id), partitionField: nameof(RunControl.Tenant));
        var repository = new SqliteEntityOutboxRepository(file.Database, mapping);
        new SqliteSchema("conformance/entity", [mapping.InitialMigration]).Apply(file.Database);
        new SqliteSchema("conformance/outbox", repository.Migrations).Apply(file.Database);
        await EntityRepositoryConformance.Verify(repository, probe);
    }
}
