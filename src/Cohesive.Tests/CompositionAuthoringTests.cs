using Cohesive.Adapters.Postgres;
using Cohesive.Api;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests;

public sealed class CompositionAuthoringTests
{
    public sealed record Item(string Id, string Partition);
    sealed class Items : DomainModel
    {
        public Items() : base("1") => ItemsEntity = Entity<Item>("items");
        public DomainEntity<Item> ItemsEntity { get; }
        public void AddLate() => Entity<Item>("late");
    }
    sealed class Queries : QueryModel
    {
        public Queries(Items domain) => ById = Add(Define(domain));
        public RelationQuery<string, Item[]> ById { get; }
        public RelationQuery<string, Item[]> Add(RelationQuery<string, Item[]> query) => Query(query);
    }
    static RelationQuery<string, Item[]> Define(Items domain)
    {
        var query = RelationQuery.Expression();
        var id = query.Parameter<string>("id");
        return query.Source(domain.ItemsEntity).Where(item => item.Id == id.Value)
            .BuildArrayQuery(new("items/by-id"), new("ItemsById"), id);
    }

    sealed class ItemApi : ApiModel
    {
        public ItemApi() : base("items") => Read = Endpoints.Query("read").Route("GET", "/items").Build<Item[]>();
        public ApiEndpoint<Item[]> Read { get; }
        public void AddLate() => Endpoints.Query("late").Build<Item>();
    }

    [Fact]
    public void Instance_api_uses_existing_contract_composition_and_freezes_endpoint_additions()
    {
        var api = new ItemApi();
        Assert.Same(api.Definition, api.Definition);
        Assert.Equal(api.Read.Id, Assert.Single(api.Definition.Endpoints).Id);
        Assert.Throws<InvalidOperationException>(api.AddLate);
        var extra = Cohesive.Api.Api.Define("inventory");
        extra.Query("read").Route("GET", "/inventory").Build<Item[]>();
        var combined = ApiDefinition.Combine([api.Definition, extra.Build()]);
        Assert.Equal(2, combined.Endpoints.Count);
        Assert.Throws<InvalidOperationException>(() => ApiDefinition.Combine([api.Definition, api.Definition]));
    }

    [Fact]
    public void Domain_freezes_exact_entities_once_and_rejects_late_contributions()
    {
        var domain = new Items();
        Assert.Same(domain.ItemsEntity.Definition, Assert.Single(domain.Definition.Entities));
        Assert.Same(domain.Definition, domain.Definition);
        Assert.Throws<InvalidOperationException>(domain.AddLate);
        var other = new Items();
        Assert.NotSame(domain.Definition, other.Definition);
    }

    [Fact]
    public void Query_catalog_retains_authority_and_rejects_duplicate_ids_before_freezing()
    {
        var domain = new Items();
        var queries = new Queries(domain);
        Assert.Throws<ArgumentException>(() => queries.Add(Define(domain)));
        Assert.Same(queries.ById.CompilationRequest, Assert.Single(queries.Definition));
        Assert.True(queries.Definition == queries.Definition);
        Assert.Throws<InvalidOperationException>(() => queries.Add(Define(domain)));
    }

    [Fact]
    public void Inline_and_extracted_native_mapping_recipes_use_the_same_builder()
    {
        var domain = new Items();
        void Configure(PostgresPersistenceBindings bindings) => bindings.Entity(domain.ItemsEntity, mapping => mapping
            .Table("public", "items").Identity(item => item.Id, "id").Partition(item => item.Partition, "partition"));
        var inline = new PostgresPersistenceBindings(new("db"));
        Configure(inline);
        var extracted = PostgresPersistenceBindings.Define(new("db"), Configure);
        Assert.Equal(inline.Mapping(domain.ItemsEntity).Fields.Select(field => (field.FieldName, field.Column.Value, field.ScalarType)),
            extracted.Mapping(domain.ItemsEntity).Fields.Select(field => (field.FieldName, field.Column.Value, field.ScalarType)));
        Assert.Throws<InvalidOperationException>(() => Configure(extracted));
        Assert.Throws<ArgumentException>(() => extracted.Mapping(new Items().ItemsEntity));
    }

    [Fact]
    public void Failed_mapping_contribution_does_not_pollute_existing_bindings()
    {
        var domain = new Items();
        var bindings = new PostgresPersistenceBindings(new("db"));
        Assert.Throws<InvalidOperationException>(() => bindings.Entity(domain.ItemsEntity, mapping => mapping.Table("public", "items")));
        bindings.Entity(domain.ItemsEntity, mapping => mapping.Table("public", "items")
            .Identity(item => item.Id, "id").Partition(item => item.Partition, "partition"));
        Assert.NotNull(bindings.Mapping(domain.ItemsEntity));
        Assert.Throws<ArgumentException>(() => bindings.Entity(domain.ItemsEntity, bindings.Mapping(domain.ItemsEntity)));
    }
}
