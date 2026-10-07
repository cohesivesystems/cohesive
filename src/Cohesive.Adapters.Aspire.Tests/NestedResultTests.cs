using System.Collections.Immutable;
using AspireFirst.Orders;
using Cohesive.Model;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class NestedResultTests
{
    static NestedQueryResultAssembly Assembly => ((QueryDefinition)OrderDetailsQuery.Definition.CompilationRequest.DefinitionDocument.Definition).Assembly!;

    [Fact]
    public void Single_declaration_handles_absence_distinct_children_ordering_and_conflicts()
    {
        Assert.Null(OrderDetailsQuery.Definition.Project([]));
        Assert.Empty(OrderDetailsQuery.Definition.Project([Row(null)])!.Reservations);
        var row = Row("b");
        var result = OrderDetailsQuery.Definition.Project([row, Row("a"), row])!;
        Assert.Equal("order", result.Id);
        Assert.Equal(new[] { "a", "b" }, result.Reservations.Select(child => child.Id));
        Assert.All(result.Reservations, child => Assert.Equal(8, child.AvailableStock));
        Assert.Throws<InvalidOperationException>(() => OrderDetailsQuery.Definition.Project([row, Row("b", quantity: 99)]));
        Assert.Throws<InvalidOperationException>(() => OrderDetailsQuery.Definition.Project([row, Row("b", parent: "another")]));
        Assert.Throws<InvalidOperationException>(() => OrderDetailsQuery.Definition.Project([row, Row("a", status: "Submitted")]));
    }

    [Fact]
    public void Assembly_is_persisted_fingerprinted_and_rejects_unknown_slots()
    {
        var document = OrderDetailsQuery.Definition.CompilationRequest.DefinitionDocument;
        var roundtrip = RelationQueryJsonSerializer.Deserialize(RelationQueryJsonSerializer.Serialize(document));
        Assert.Equal(document.DefinitionFingerprint, roundtrip.DefinitionFingerprint);
        var definition = (QueryDefinition)roundtrip.Definition;
        Assert.Equal(NestedQueryResultAssembler.Assemble(Assembly, [Row("a")]),
            NestedQueryResultAssembler.Assemble(definition.Assembly!, [Row("a")]));
        var changed = definition with { Assembly = Assembly with { Identity = Assembly.Fields[1].Source } };
        Assert.NotEqual(document.DefinitionFingerprint, RelationQueryDefinitionFingerprinter.Compute(changed));
        var invalid = definition with { Assembly = Assembly with { Identity = FieldPath.FromField("unknown") } };
        Assert.False(RelationQueryDefinitionValidator.Validate(invalid).IsValid);
        Assert.Throws<ArgumentException>(() => RelationQueryDocument.FromDefinition(invalid));
    }

    [Fact]
    public void Assembly_rejects_incomplete_shapes_invalid_scalars_and_cancellation()
    {
        var request = OrderDetailsQuery.Definition.CompilationRequest;
        var definition = (QueryDefinition)request.DefinitionDocument.Definition;
        var invalid = definition with { Assembly = Assembly with { Fields = [Assembly.Fields[0]] } };
        var compilation = RelationQueryStaticCompiler.Compile(new(RelationQueryDocument.FromDefinition(invalid),
            request.ShapeDocuments, request.RelationshipCatalogDocument));
        Assert.False(compilation.IsSuccessful);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "relationQuery.query.assemblyInvalid");
        Assert.Throws<OperationCanceledException>(() => NestedQueryResultAssembler.Assemble(Assembly, [], new(true)));
        Assert.Throws<InvalidOperationException>(() => OrderDetailsQuery.Definition.Project(
            [Row("a").WithField(Assembly.Fields[1].Source, ObservationValue.Null)]));
        Assert.Throws<InvalidOperationException>(() => OrderDetailsQuery.Definition.Project(
            [Row("a").WithField(Assembly.Identity, ObservationValue.FromObject(42))]));
    }

    [Fact]
    public void Bound_nodes_and_explicit_bindings_lower_to_the_same_query()
    {
        var nodes = Define(true);
        var bindings = Define(false);
        Assert.Equal(bindings.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            nodes.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        var definition = (QueryDefinition)nodes.CompilationRequest.DefinitionDocument.Definition;
        var assembly = definition.Assembly!;
        Assert.Equal(assembly.Identity, assembly.Fields[0].Source);
        Assert.Equal(3, definition.Body.Nodes.OfType<ProjectQueryNode>().Single().Assignments.Length);

        static RelationQuery<string, OrderHeader?> Define(bool useNodes)
        {
            var author = RelationQuery.Expression();
            var id = author.Parameter<string>("id");
            var source = useNodes ? author.Source(FulfillmentDomain.Orders)
                : author.Source(FulfillmentDomain.Orders.QueryShape(author));
            var orders = author.Where(source, order => order.Id == id.Value);
            var reservations = author.TraverseInverse(orders, FulfillmentDomain.ReservationOrder);
            var result = author.SingleOrDefault<OrderHeader>();
            if (useNodes) result.From(reservations, orders, order => order.Id).Identity(header => header.Id)
                .Field(header => header.Status, orders, order => order.Status)
                .Collection(header => header.Reservations, reservations, reservation => reservation.Id,
                    child => child.Identity(item => item.Id));
            else result.From(reservations, orders.Binding, order => order.Id).Identity(header => header.Id)
                .Field(header => header.Status, orders.Binding, order => order.Status)
                .Collection(header => header.Reservations, reservations.Binding, reservation => reservation.Id,
                    child => child.Identity(item => item.Id));
            var query = result.Build(new("test/ergonomics"), new("Ergonomics"), id);
            Assert.Throws<InvalidOperationException>(() => result.Identity(header => header.Id));
            return query;
        }
    }

    [Fact]
    public void Identity_reuses_parent_and_child_slots_and_foreign_nodes_still_fail()
    {
        Assert.Equal(Assembly.Identity, Assembly.Fields.Single(field => field.Target.ToString() == "Id").Source);
        var child = Assert.Single(Assembly.Collections);
        Assert.Equal(child.Identity, child.Fields.Single(field => field.Target.ToString() == "Id").Source);
        var definition = (QueryDefinition)OrderDetailsQuery.Definition.CompilationRequest.DefinitionDocument.Definition;
        Assert.Equal(6, definition.Body.Nodes.OfType<ProjectQueryNode>().Single().Assignments.Length);

        var author = RelationQuery.Expression();
        var order = author.Source(FulfillmentDomain.Orders);
        var foreign = RelationQuery.Expression().Source(FulfillmentDomain.Orders);
        Assert.Throws<ArgumentException>(() => author.SingleOrDefault<OrderHeader>().From(order, foreign, value => value.Id));
        var result = author.SingleOrDefault<OrderHeader>().From(order, order, value => value.Id);
        Assert.Throws<ArgumentException>(() => result.Field(header => header.Status, foreign, value => value.Status));
        Assert.Throws<InvalidOperationException>(() => author.SingleOrDefault<OrderHeader>().Identity(header => header.Id));
    }

    public sealed record OrderHeader(string Id, string Status, IReadOnlyList<ReservationHeader> Reservations);
    public sealed record ReservationHeader(string Id);

    static ObservationValue Row(string? child, string parent = "order", string status = "Draft", int quantity = 2)
    {
        var assembly = Assembly;
        var value = ObservationValue.FromObject(new Dictionary<string, ObservationValue>());
        value = value.WithField(assembly.Identity, ObservationValue.FromString(parent));
        foreach (var field in assembly.Fields)
            value = value.WithField(field.Source, ObservationValue.FromString(field.Target.ToString() == "Id" ? parent : status));
        if (child is null) return value;
        var collection = assembly.Collections[0];
        value = value.WithField(collection.Identity, ObservationValue.FromString(child));
        foreach (var field in collection.Fields)
            value = value.WithField(field.Source, field.Target.ToString() switch
            {
                "Id" => ObservationValue.FromString(child),
                "Sku" => ObservationValue.FromString("book"),
                "Quantity" => ObservationValue.FromObject(quantity),
                "AvailableStock" => ObservationValue.FromObject(8),
                _ => throw new InvalidOperationException()
            });
        return value;
    }
}
