using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Migrations;

namespace Cohesive.Relations.Tests;

public sealed class SchemaMappingTests
{
    sealed record Order(string Id, string CustomerId);
    sealed record Customer(string RegionId);
    sealed record Region(string Code, string Name, string? OptionalCode);
    sealed record OrderSummary(string Id, string RegionCode, string RegionName);

    sealed class IdentityContract : IClrShapeMetadataProvider
    {
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context) =>
            context.Property?.Name == nameof(Order.Id) ? new() { FieldRole = FieldRole.Identity } : ClrShapeMetadata.Empty;
    }

    readonly ClrShapeGraphBuildResult domain = new ClrShapeGraphBuilder().AddMetadataProvider(new IdentityContract())
        .AddShape<OrderSummary>().BuildResult(new("summary/v1"));
    readonly ClrShapeGraphBuildResult sales = new ClrShapeGraphBuilder().AddMetadataProvider(new IdentityContract())
        .AddShape<Order>().AddShape<Customer>().AddShape<Region>().BuildResult(new("sales/v3"));
    readonly RelationQueryExpressionRelationship<Order, Customer> customer;
    readonly RelationQueryExpressionRelationship<Customer, Region> region;
    readonly RelationshipCatalog catalog;
    readonly RelationId id = new("order-summary-over-sales");

    public SchemaMappingTests()
    {
        customer = new(Relationship.From<Order>(sales).Reference(o => o.CustomerId).To(sales.GetShape<Customer>()));
        region = new(Relationship.From<Customer>(sales).Reference(c => c.RegionId).To(sales.GetShape<Region>()));
        catalog = new([customer.Definition, region.Definition]);
    }

    [Fact]
    public void Restored_mapping_lowers_to_the_hand_authored_relation_with_shared_traversals()
    {
        var mapping = Define((summary, order) =>
        {
            summary.Field(s => s.RegionName, order.Via(customer).Via(region), r => r.Name);
            summary.Field(s => s.RegionCode, order.Via(customer).Via(region), r => r.Code);
        });
        var options = StrictDocumentJson.CreateOptions();
        var restored = JsonSerializer.Deserialize<SchemaMappingDocument>(StrictDocumentJson.GetCanonicalText(mapping, options), options)!;
        Assert.Equal(mapping.Fingerprint(), restored.Fingerprint());
        var reordered = Define((summary, order) =>
        {
            var endpoint = order.Via(customer).Via(region);
            summary.Field(s => s.RegionCode, endpoint, r => r.Code);
            summary.Field(s => s.RegionName, endpoint, r => r.Name);
        });
        Assert.Equal(mapping.Fingerprint(), reordered.Fingerprint());

        var hand = RelationQuery.Structural();
        var orders = hand.Source(sales.GetShape<Order>());
        var customers = hand.Traverse(orders.Node, orders.Binding, customer.Id);
        var regions = hand.Traverse(customers.Node, customers.Binding, region.Id);
        var shape = domain.GetShape<OrderSummary>();
        var summaries = hand.Project(regions.Node, shape, [
            new(FieldPath.FromField("Id"), orders.Binding.Field("Id")),
            new(FieldPath.FromField("RegionCode"), regions.Binding.Field("Code")),
            new(FieldPath.FromField("RegionName"), regions.Binding.Field("Name"))
        ]);
        var expected = hand.BuildRelation(id, new(id.Value), orders.Binding, summaries.Node, shape,
            RelationOutputMode.OnePerRoot, summaries.Binding.Field("Id"));
        var actual = restored.Pullback();
        Assert.True(actual.Validation.IsValid);
        Assert.Equal(RelationQueryJsonSerializer.Serialize(expected.CreateDocument()), RelationQueryJsonSerializer.Serialize(actual.CreateDocument()));
        Assert.Equal(expected.CreateDocument().DefinitionFingerprint, actual.CreateDocument().DefinitionFingerprint);
    }

    [Fact]
    public void Rejects_missing_fields_duplicate_identity_and_foreign_paths()
    {
        Assert.Throws<InvalidOperationException>(() => Define((_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => Define((summary, order) =>
            summary.Field(s => s.Id, order, o => o.Id)));
        MappingPath<Order>? foreign = null;
        Assert.Throws<InvalidOperationException>(() => Define((_, order) => foreign = order));
        Assert.Throws<ArgumentException>(() => Define((summary, _) =>
            summary.Field(s => s.RegionCode, foreign!, o => o.Id)));
    }

    [Fact]
    public void Rejects_optional_source_field_and_unpinned_relationship()
    {
        var optional = Assert.Throws<InvalidOperationException>(() => Define((summary, order) =>
            summary.Field(s => s.RegionCode, order.Via(customer).Via(region), r => r.OptionalCode)));
        Assert.Contains(nameof(Region.OptionalCode), optional.Message);
        var foreign = new RelationQueryExpressionRelationship<Order, Customer>(
            Relationship.From<Order>(sales).Reference(o => o.CustomerId).To(sales.GetShape<Customer>(), new("another-edge")));
        Assert.Throws<ArgumentException>(() => Define((summary, order) =>
            summary.Field(s => s.RegionCode, order.Via(foreign), c => c.RegionId)));
    }

    [Fact]
    public void Identity_requires_schema_evidence_and_lowers_without_a_traversal()
    {
        var unmarked = new ClrShapeGraphBuilder().AddShape<Order>().BuildResult(new("unmarked"));
        Assert.Throws<InvalidOperationException>(() => SchemaMapping.Define<Order, Order>(id, unmarked, sales,
            catalog, (target, order) => target.Field(o => o.CustomerId, order, o => o.CustomerId)));

        var copy = SchemaMapping.Define<Order, Order>(id, sales, sales, catalog,
            (target, order) => target.Field(o => o.CustomerId, order, o => o.CustomerId));
        var result = copy.Pullback();
        Assert.True(result.Validation.IsValid);
        Assert.Empty(result.Definition.Body.Nodes.OfType<TraverseRelationshipQueryNode>());
    }

    SchemaMappingDocument Define(Action<MappingExtent<OrderSummary>, MappingPath<Order>> define) =>
        SchemaMapping.Define(id, domain, sales, catalog, define);
}
