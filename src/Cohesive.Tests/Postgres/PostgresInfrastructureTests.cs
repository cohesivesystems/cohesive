using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Infra;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresInfrastructureTests
{
    public sealed record Item(string Id, string Partition);
    public sealed record Row(string Id);
    static readonly DomainEntity<Item> Entity = new DomainModelBuilder().Entity<Item>("test/item");
    static PostgresPersistenceBindings Bindings(string database = "orders", string table = "items") =>
        new PostgresPersistenceBindings(new(database)).Entity(Entity, PostgresEntityRepositoryMapping.For(Entity)
            .Table("public", table).Identity(item => item.Id, "id").Partition(item => item.Partition, "partition").Build());

    static RelationQuery<string, Row[]> Query(string id = "query/items")
    {
        var query = RelationQuery.Expression();
        var key = query.Parameter<string>("key");
        return query.Source(Entity).Where(item => item.Id == key.Value && item.Partition == "local")
            .Select(item => new Row(item.Id)).BuildArrayQuery(new(id), new("Items"), key);
    }

    [Fact]
    public void Infrastructure_compiler_rejects_evidence_for_a_different_query()
    {
        var required = PostgresInfrastructure.QuerySnapshot(Query().CompilationRequest);
        var definition = Infrastructure.Define(new("test/model"), new("1"), new("test/bindings"),
            infra => infra.Resource(new("store")).Persistent().Requires(required));
        var wrong = PostgresInfrastructure.QueryImplementation(new("test/postgres"),
            Bindings().Prepare(Query("query/different").CompilationRequest));
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment"), definition.Definition,
            new("test/facilities"), new("test/profile"), new("test/target"), new("test/variant"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion], deployment => deployment.ResourceUsing(
                new("store"), wrong, new("test/native-store"), new("test/authority"),
                [SourceReference.Create("test", "query-evidence")]));
        Assert.False(InfrastructureTargetDeploymentCompiler.Compile(definition, manifest).IsComplete);
    }

    [Fact]
    public void Offline_preparation_and_runtime_registration_produce_identical_native_artifacts()
    {
        var query = Query();
        var bindings = Bindings();
        var offline = bindings.Prepare(query.CompilationRequest);
        using var data = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var runtime = bindings.Bind(new(new("orders"), data, "test"));
        Assert.Equal(offline.Fingerprint, runtime.Query(query, 10, 1000).Artifact.Fingerprint);
        Assert.Equal(Entity.Definition, runtime.Repository(Entity).EntityDefinition);
    }

    [Fact]
    public void Evidence_is_exact_to_semantic_plan_and_changes_with_native_mapping()
    {
        var query = Query();
        var required = PostgresInfrastructure.QuerySnapshot(query.CompilationRequest);
        var first = PostgresInfrastructure.QueryImplementation(new("test/postgres"), Bindings().Prepare(query.CompilationRequest));
        var moved = PostgresInfrastructure.QueryImplementation(new("test/postgres"), Bindings(table: "other_items").Prepare(query.CompilationRequest));
        Assert.Equal(required, Assert.Single(first.Evidence).Capability);
        Assert.Equal(required, Assert.Single(moved.Evidence).Capability);
        Assert.NotEqual(first.Evidence[0].Id, moved.Evidence[0].Id);
        Assert.NotEqual(first.Evidence[0].SourceReferences, moved.Evidence[0].SourceReferences);
        Assert.NotEqual(required, PostgresInfrastructure.QuerySnapshot(Query("query/other").CompilationRequest));
    }

    [Fact]
    public void Missing_mapping_and_mismatched_runtime_fail_before_io()
    {
        Assert.Throws<InvalidOperationException>(() => new PostgresPersistenceBindings(new("orders")).Prepare(Query().CompilationRequest));
        using var data = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        Assert.Throws<ArgumentException>(() => Bindings().Bind(new(new("other"), data, "test")));
    }

    [Fact]
    public void Evidence_rejects_empty_duplicate_and_cross_database_artifacts()
    {
        var query = Query();
        var first = Bindings().Prepare(query.CompilationRequest);
        var other = Bindings("inventory").Prepare(Query("query/other").CompilationRequest);
        Assert.Throws<ArgumentException>(() => PostgresInfrastructure.QueryImplementation(new("test/postgres")));
        Assert.Throws<ArgumentException>(() => PostgresInfrastructure.QueryImplementation(new("test/postgres"), first, first));
        Assert.Throws<ArgumentException>(() => PostgresInfrastructure.QueryImplementation(new("test/postgres"), first, other));
    }

    [Fact]
    public void Runtime_binding_is_a_snapshot_of_offline_mappings()
    {
        var bindings = new PostgresPersistenceBindings(new("orders"));
        using var data = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var runtime = bindings.Bind(new(new("orders"), data, "test"));
        bindings.Entity(Entity, PostgresEntityRepositoryMapping.For(Entity).Table("public", "items")
            .Identity(item => item.Id, "id").Partition(item => item.Partition, "partition").Build());
        Assert.Throws<InvalidOperationException>(() => runtime.Repository(Entity));
        Assert.NotNull(bindings.Prepare(Query().CompilationRequest));
    }
}
