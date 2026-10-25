using Cohesive.Relations.Execution;
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
    static PostgresPersistenceBindings Bindings() => PostgresPersistenceBindings.Define(new("items"), bindings => bindings
        .Entity(Entity, PostgresEntityRepositoryMapping.For(Entity).Table("public", "items")
            .Identity(item => item.Id, "id").Partition(item => item.Partition, "partition").Build()));
    static ServiceBuilder Service(ServiceDependencies dependencies) => Cohesive.Api.Execution.Services.Service
        .Define(new("test"), new("1"), RunControlFixture.Provenance).Use(dependencies);

    sealed class QueryApi : Cohesive.Api.ApiModel
    {
        internal QueryApi() => Read = Endpoints.Query("items").Route("GET", "/items").Build<Row[]>();
        internal Cohesive.Api.ApiEndpoint<Row[]> Read { get; }
    }

    [Fact]
    public void Portable_endpoint_plan_drives_native_evidence_without_a_service_catalog()
    {
        var api = new QueryApi();
        var plan = new ServiceImplementationPlan();
        plan.Declare(api.Definition);
        var reference = plan.Query(Query(), new("2"), 7, 1000, RelationQueryConsistency.SingleSnapshot);
        plan.Implement(api.Definition, api.Read, reference);

        var evidence = Bindings().PrepareService(plan, new("items/postgres"));

        Assert.Equal("relations/query-snapshot/v1/items%2Fread/2", Assert.Single(evidence.Requirements).Value);
        Assert.Equal(7, Assert.Single(plan.QueryDependencies.Queries).MaximumRows);
    }

    [Fact]
    public void Incomplete_endpoint_plan_is_rejected_before_native_preparation()
    {
        var plan = new ServiceImplementationPlan();
        plan.Declare(new QueryApi().Definition);

        var failure = Assert.Throws<ServiceBindingValidationException>(() =>
            Bindings().PrepareService(plan, new("items/postgres")));

        Assert.Equal("services.api.missingImplementation", Assert.Single(failure.Validation.Diagnostics).Code);
    }

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
    public void Native_snapshot_satisfies_independent_source_tolerance()
    {
        var query = Query();
        var service = Service(new ServiceDependencies().Query(query, new("1"), 10, 1000, RelationQueryConsistency.IndependentSources));
        Assert.Single(Bindings().PrepareService(service, new("items/postgres")).Requirements);
        using var database = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused;Username=test");
        var persistence = Bindings().Bind(new(new("items"), database, "test"));
        Assert.Equal(RelationQueryConsistency.SingleSnapshot, persistence.Query(service, query).Consistency);
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
