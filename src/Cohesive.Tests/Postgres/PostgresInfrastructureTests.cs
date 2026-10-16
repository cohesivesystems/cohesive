using Cohesive.Relations.Compilation;
using Cohesive.Relations.Infra;
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
        PostgresPersistenceBindings.Define(new(database), bindings => bindings.Entity(Entity, PostgresEntityRepositoryMapping.For(Entity)
            .Table("public", table).Identity(item => item.Id, "id").Partition(item => item.Partition, "partition").Build()));

    static RelationQuery<string, Row[]> Query(string id = "query/items")
    {
        var query = RelationQuery.Expression();
        var key = query.Parameter<string>("key");
        return query.Source(Entity).Where(item => item.Id == key.Value && item.Partition == "local")
            .Select(item => new Row(item.Id)).BuildArrayQuery(new(id), new("Items"), key);
    }

    static QuerySnapshotRequirement Requirement(string id = "query/items") => new(id, "1", Query(id).CompilationRequest);

    [Fact]
    public void Infrastructure_compiler_rejects_evidence_for_a_different_query()
    {
        var required = Requirement().Capability;
        var definition = Infrastructure.Define(new("test/model"), new("1"), new("test/bindings"),
            infra => infra.Resource(new("store")).Persistent().Requires(required));
        var wrong = PostgresInfrastructure.QueryImplementation(new("test/postgres"),
            (Requirement("query/different"), Bindings().Prepare(Query("query/different").CompilationRequest)));
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment"), definition.Definition,
            new("test/facilities"), new("test/profile"), new("test/target"), new("test/variant"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion], deployment => deployment.ResourceUsing(
                new("store"), wrong.ForDatabase("orders"), new("test/native-store"), new("test/authority"),
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
        var required = Requirement().Capability;
        var first = PostgresInfrastructure.QueryImplementation(new("test/postgres"), (Requirement(), Bindings().Prepare(query.CompilationRequest)));
        var moved = PostgresInfrastructure.QueryImplementation(new("test/postgres"), (Requirement(), Bindings(table: "other_items").Prepare(query.CompilationRequest)));
        Assert.Equal(required, Assert.Single(first.ForDatabase("orders").Evidence).Capability);
        Assert.Equal(required, Assert.Single(moved.ForDatabase("orders").Evidence).Capability);
        Assert.NotEqual(first.ForDatabase("orders").Evidence[0].Id, moved.ForDatabase("orders").Evidence[0].Id);
        Assert.NotEqual(first.ForDatabase("orders").Evidence[0].SourceReferences, moved.ForDatabase("orders").Evidence[0].SourceReferences);
        Assert.NotEqual(required, Requirement("query/other").Capability);
    }

    [Fact]
    public void Missing_mapping_and_mismatched_runtime_fail_before_io()
    {
        Assert.Throws<InvalidOperationException>(() => PostgresPersistenceBindings.Define(new("orders"), _ => { }).Prepare(Query().CompilationRequest));
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
        Assert.Throws<ArgumentException>(() => PostgresInfrastructure.QueryImplementation(new("test/postgres"), (Requirement(), first), (Requirement(), first)));
        Assert.Throws<ArgumentException>(() => PostgresInfrastructure.QueryImplementation(new("test/postgres"), (Requirement(), first), (Requirement("query/other"), other)));
    }

    [Fact]
    public void Runtime_binding_retains_frozen_offline_mappings()
    {
        var bindings = Bindings();
        using var data = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var runtime = bindings.Bind(new(new("orders"), data, "test"));
        Assert.Throws<InvalidOperationException>(() => bindings.Entity(Entity, bindings.Mapping(Entity)));
        Assert.NotNull(runtime.Repository(Entity));
    }
    [Fact]
    public void Requirement_identity_is_explicit_and_independent_of_plan_compilation()
    {
        var first = new QuerySnapshotRequirement("orders/read", "7", Query().CompilationRequest);
        var changed = new QuerySnapshotRequirement("orders/read", "7", Query("different").CompilationRequest);
        Assert.Equal(first.Capability, changed.Capability);
        Assert.Equal("relations/query-snapshot/v1/orders%2Fread/7", first.Capability.Value);
        Assert.NotEqual(first.Capability, new QuerySnapshotRequirement("orders/read", "8", Query().CompilationRequest).Capability);
        Assert.Throws<ArgumentException>(() => PostgresInfrastructure.QueryImplementation(new("test/postgres"),
            (first, Bindings().Prepare(changed.Request))));
    }

    [Fact]
    public void Preparation_exposes_one_requirement_set_and_checks_database_affinity()
    {
        var evidence = PostgresInfrastructure.Prepare(new("test/postgres"), Bindings(), Requirement());
        Assert.Equal(Requirement().Capability, Assert.Single(evidence.Requirements));
        Assert.NotNull(evidence.ForDatabase("orders"));
        Assert.Throws<ArgumentException>(() => evidence.ForDatabase("inventory"));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("paged")]
    [InlineData("source")]
    public void Rehydrated_artifacts_cannot_bypass_complete_plan_evidence(string invalid)
    {
        var artifact = Bindings().Prepare(Query().CompilationRequest);
        var original = artifact.StorageBinding;
        var storage = new PostgresRelationQueryStorageBinding(original.SchemaVersion, null,
            original.DatabaseSemanticsProfile, original.Id, original.Database, original.Target, original.TargetProfile,
            original.Tables, original.Origin, original.ConventionSetVersion, original.ConfigurationDecisions,
            invalid == "plan" ? new("sha256", "test/other-plan", "other") : original.CompiledPlanFingerprint,
            original.PlacementFingerprint, original.OwnedCollections,
            invalid == "source" ? new("postgres/query") : original.SourceScope);
        var paging = invalid == "paged" ? new PostgresRelationQueryPagingContract(
            PostgresRelationQueryPagingKind.Offset, 10, 0, [artifact.SelectedFields[0].Input]) : artifact.Paging;
        var fingerprint = PostgresRelationQueryArtifactFingerprinter.Compute(artifact.SchemaVersion, artifact.Branch,
            artifact.Statement, storage, artifact.SelectedFields, artifact.ResultFields, artifact.PresenceBindings,
            artifact.SuppliedFields, artifact.Parameters, paging, artifact.RelationKey, artifact.Invariants,
            artifact.LoweringDecisions, artifact.Provenance);
        var rehydrated = new PostgresRelationQueryCompiledArtifact(artifact.SchemaVersion, artifact.Branch,
            artifact.Statement, storage, artifact.SelectedFields, artifact.ResultFields, artifact.PresenceBindings,
            artifact.SuppliedFields, artifact.Parameters, paging, artifact.RelationKey, artifact.Invariants,
            artifact.LoweringDecisions, artifact.Provenance, fingerprint);
        Assert.Throws<ArgumentException>(() => PostgresInfrastructure.QueryImplementation(new("test/postgres"), (Requirement(), rehydrated)));
    }

    [Fact]
    public void Semantic_failure_uses_the_same_preparation_diagnostic_at_both_entry_points()
    {
        var request = new RelationQueryCompilationRequest(Query().CompilationRequest.DefinitionDocument);
        var declaration = Assert.Throws<RelationQueryPreparationException>(() => new QuerySnapshotRequirement("test/read", "1", request));
        var preparation = Assert.Throws<RelationQueryPreparationException>(() => Bindings().Prepare(request));
        Assert.Equal(declaration.Code, preparation.Code);
        Assert.Equal(declaration.Message, preparation.Message);
    }

}
