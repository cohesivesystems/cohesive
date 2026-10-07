using System.Collections.Immutable;
using AspireFirst.Orders;
using Cohesive.Model;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

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
