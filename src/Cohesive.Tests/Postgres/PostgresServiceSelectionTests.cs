using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresServiceSelectionTests
{
    public sealed record Item(string Id, string Partition);
    public sealed record Row(string Id);
    static readonly DomainEntity<Item> Entity = new DomainModelBuilder().Entity<Item>("item");
    static RelationQuery<string, Row[]> Query(string id = "items/read")
    {
        var query = RelationQuery.Expression();
        var key = query.Parameter<string>("id");
        return query.Source(Entity).Where(item => item.Id == key.Value && item.Partition == "local")
            .Select(item => new Row(item.Id)).BuildArrayQuery(new(id), new("Read"), key);
    }
    static PostgresPersistenceBindings Bindings() => new PostgresPersistenceBindings(new("items"))
        .Entity(Entity, PostgresEntityRepositoryMapping.For(Entity).Table("public", "items")
            .Identity(item => item.Id, "id").Partition(item => item.Partition, "partition").Build());
    static ServiceBuilder Service(ServiceDependencies dependencies) => Cohesive.Api.Execution.Services.Service
        .Define(new("test"), new("1"), RunControlFixture.Provenance).Use(dependencies);

    [Fact]
    public void Same_selected_queries_drive_reader_preparation_and_infrastructure_evidence()
    {
        var query = Query();
        var service = Service(new ServiceDependencies().Query(query, new("2"), 7, 1000));
        var bindings = Bindings();
        var evidence = bindings.PrepareService(service, new("items/postgres"));
        Assert.Equal("relations/query-snapshot/v1/items%2Fread/2", Assert.Single(evidence.Requirements).Value);
        using var database = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused;Username=test");
        var persistence = bindings.Bind(new(new("items"), database, "test"));
        var reader = persistence.Query(service, query);
        Assert.Equal(bindings.Prepare(query.CompilationRequest).Fingerprint, reader.Artifact.Fingerprint);
        Assert.Equal(7, Assert.Single(service.Dependencies.Queries).MaximumRows);
        Assert.Equal(1000, Assert.Single(service.Dependencies.Queries).MaximumBytes);
    }

    [Fact]
    public void Unselected_query_is_rejected_before_io_and_cannot_appear_in_evidence()
    {
        var service = Service(new ServiceDependencies().Query(Query(), new("1"), 10, 1000));
        using var database = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused;Username=test");
        var persistence = Bindings().Bind(new(new("items"), database, "test"));
        var failure = Assert.Throws<ServiceBindingValidationException>(() => persistence.Query(service, Query("unused")));
        Assert.Equal("services.binding.queryUnselected", Assert.Single(failure.Validation.Diagnostics).Code);
        Assert.Single(Bindings().PrepareService(service, new("items/postgres")).Requirements);
    }

    [Fact]
    public void Independent_source_selection_cannot_be_certified_or_bound_as_native_snapshot()
    {
        var query = Query();
        var service = Service(new ServiceDependencies().Query(query, new("1"), 10, 1000, ServiceQueryConsistency.IndependentSources));
        var failure = Assert.Throws<ServiceBindingValidationException>(() => Bindings().PrepareService(service, new("items/postgres")));
        Assert.Equal("services.binding.queryPlacementUnsupported", Assert.Single(failure.Validation.Diagnostics).Code);
        using var database = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused;Username=test");
        var persistence = Bindings().Bind(new(new("items"), database, "test"));
        Assert.Throws<ServiceBindingValidationException>(() => persistence.Query(service, query));
    }

    [Fact]
    public void Query_registration_is_immutable_and_rejects_duplicate_identity_and_invalid_bounds()
    {
        var empty = new ServiceDependencies();
        var selected = empty.Query(Query(), new("1"), 10, 1000);
        Assert.Empty(empty.Queries);
        Assert.Single(selected.Queries);
        Assert.Throws<ServiceBindingValidationException>(() => selected.Query(Query(), new("2"), 10, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Query(Query(), new("1"), 0, 1000));
        Assert.Empty(empty.Queries);
    }
}
