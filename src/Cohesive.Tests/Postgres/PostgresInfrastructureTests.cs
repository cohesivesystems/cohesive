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
    public sealed record OptionalRow(string? Id);
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

    [PostgresProcessDurableStoreTests.PostgresFact]
    public async Task Synchronous_target_repairs_rows_and_preserves_publication_on_failure()
    {
        await using var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!);
        var suffix = Guid.NewGuid().ToString("N");
        var source = "items_" + suffix;
        var target = "projected_" + suffix;
        var registration = Bindings(table: source).Bind(db, authority: "tests");
        var projection = registration.MaterializeRows(Query(), "a", target);
        async Task Execute(string sql)
        {
            await using var command = db.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        async Task<object?> Read()
        {
            await using var command = db.CreateCommand($"SELECT string_agg(id, ',') FROM public.{target}");
            return await command.ExecuteScalarAsync();
        }
        try
        {
            await Execute($"CREATE TABLE public.{source} (id text, partition text); INSERT INTO public.{source} VALUES ('a','local'),('b','local')");
            await projection.ResyncAsync();
            Assert.Equal("a", await Read());
            await Execute($"UPDATE public.{target} SET id='stale'");
            var byId = projection.Lookup(row => row.Id);
            Assert.Equal(new Row("stale"), Assert.Single(await byId.ReadAsync("stale")));
            Assert.Empty(await byId.ReadAsync("a"));
            Assert.Empty(await byId.ReadAsync("a' OR true --"));
            await Task.WhenAll(projection.ResyncAsync(), projection.ResyncAsync());
            Assert.Equal("a", await Read());
            var mismatched = registration.MaterializeRows(Query(), "b", target);
            await Assert.ThrowsAsync<InvalidOperationException>(() => mismatched.ResyncAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => mismatched.Lookup(row => row.Id).ReadAsync("a"));
            Assert.Equal("a", await Read());
            await Execute($"DROP TABLE public.{source}");
            Assert.Equal(new Row("a"), Assert.Single(await byId.ReadAsync("a")));
            await Assert.ThrowsAsync<PostgresException>(() => projection.ResyncAsync());
            Assert.Equal("a", await Read());
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS public.{target},public.{source}");
        }
    }

    [PostgresProcessDurableStoreTests.PostgresFact]
    public async Task Typed_lookup_preserves_matching_rows_and_handles_a_null_key()
    {
        await using var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!);
        var suffix = Guid.NewGuid().ToString("N");
        var source = "items_" + suffix;
        var target = "lookup_" + suffix;
        var query = RelationQuery.Expression();
        var partition = query.Parameter<string>("partition");
        var definition = query.Source(Entity).Where(item => item.Partition == partition.Value)
            .Select(item => new OptionalRow(item.Id)).BuildArrayQuery(new("query/optional"), new("Optional"), partition);
        var projection = Bindings(table: source).Bind(db, authority: "tests").MaterializeRows(definition, "local", target);
        var lookup = projection.Lookup(row => row.Id);
        try
        {
            await using var create = db.CreateCommand($"CREATE TABLE public.{source} (id text, partition text); INSERT INTO public.{source} VALUES ('a','local'), ('a','local')");
            await create.ExecuteNonQueryAsync();
            await projection.ResyncAsync();
            Assert.Equal([new OptionalRow("a"), new OptionalRow("a")], await lookup.ReadAsync("a"));
            await using var damage = db.CreateCommand($"UPDATE public.{target} SET id = NULL");
            await damage.ExecuteNonQueryAsync();
            Assert.Equal([new OptionalRow(null), new OptionalRow(null)], await lookup.ReadAsync(null));
        }
        finally
        {
            await using var cleanup = db.CreateCommand($"DROP TABLE IF EXISTS public.{target},public.{source}");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

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
    public void Direct_data_source_binding_reuses_declared_identity_and_native_artifact()
    {
        var bindings = Bindings("declared-orders");
        using var data = NpgsqlDataSource.Create("Host=invalid.invalid;Database=physical_name;Username=test");
        var explicitRuntime = bindings.Bind(new PostgresNpgsqlRuntimeBinding(bindings.Database, data, "test/binding"));
        var direct = bindings.Bind(data, authority: "test/binding");
        Assert.Equal(explicitRuntime.Query(Query(), 10, 1000).Artifact.Fingerprint,
            direct.Query(Query(), 10, 1000).Artifact.Fingerprint);
        Assert.Same(Entity.Definition, direct.Repository(Entity).EntityDefinition);
    }

    [Fact]
    public void Direct_data_source_binding_preserves_runtime_validation()
    {
        var bindings = Bindings();
        using var data = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused;Username=test");
        using var multiHost = new NpgsqlDataSourceBuilder("Host=primary,secondary;Database=unused;Username=test").BuildMultiHost();
        Assert.Throws<ArgumentNullException>(() => bindings.Bind((NpgsqlDataSource)null!, "test"));
        Assert.Throws<ArgumentNullException>(() => bindings.Bind(data, null!));
        Assert.Throws<ArgumentException>(() => bindings.Bind(data, "Password=not-an-authority"));
        Assert.Throws<ArgumentException>(() => bindings.Bind(multiHost, "test"));
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
