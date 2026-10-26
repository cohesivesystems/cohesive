using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Authoring;

/// <summary>A rooted row relation with portable per-root nested-result assembly.</summary>
/// <typeparam name="TResult">CLR projection of the declared nested result shape.</typeparam>
/// <remarks>The retained relation is the shared definition. Keyed queries add only invocation selection;
/// neither path owns a second set of joins, filters or field mappings.</remarks>
public sealed class RootedRelation<TResult> where TResult : class
{
    internal RootedRelation(RelationQueryCompilationRequest definition) => Definition = definition;

    /// <summary>Canonical parameter-free relation, shape snapshots and relationships used for hydration.</summary>
    public RelationQueryCompilationRequest Definition { get; }

    /// <summary>Derives a keyed live read without changing the relation's joins, filters or result assembly.</summary>
    /// <param name="id">Stable identity of the derived query.</param>
    /// <returns>A query returning null for an absent or filtered root.</returns>
    /// <exception cref="ArgumentException">A generated identity collides with a declaration, or the relation lacks a direct root key.</exception>
    public RelationQuery<string, TResult?> ReadById(QueryId id)
    {
        var relation = (RelationDefinition)Definition.DefinitionDocument.Definition;
        var assembly = relation.Assembly!;
        var projection = (ProjectQueryNode)relation.Body.Nodes.Single(node => node.Id == relation.Output.Node);
        var key = projection.Assignments.Single(assignment => assignment.Target == assembly.Identity).Value;
        if (key is not FieldExpr field || field.Binding != relation.RootBinding)
            throw new ArgumentException("Keyed reads require a direct root identity field.", nameof(id));
        var source = relation.Body.Nodes.OfType<SourceQueryNode>().Single(node => node.Binding == relation.RootBinding);
        QueryNodeId scanId = new(id.Value + "/root-source");
        if (relation.Body.Nodes.Any(node => node.Id == scanId))
            throw new ArgumentException("Derived root-source identity collides with the relation graph.", nameof(id));
        QueryParameterId parameter = new("rootId");
        // Keep the old source-node identity on the filter so every downstream edge is preserved.
        var body = new LogicalQueryDefinition(
            [.. relation.Body.Nodes.Where(node => node.Id != source.Id), source with { Id = scanId },
                new FilterQueryNode(source.Id, scanId, new BinaryExpr(BinaryOperator.Eq, key, new ParameterExpr(parameter.Value)))],
            [new(parameter, new ScalarTypeRef(ScalarTypeKind.String))]);
        var query = new QueryDefinition(id, new(id.Value), body, [new RowsQueryResultDefinition(assembly.Result, projection.Id)])
        { Assembly = assembly };
        var document = RelationQueryDocument.FromDefinition(query, new(
            origin: DocumentOrigin.Generated,
            sourceUri: "cohesive-relation://" + Uri.EscapeDataString(relation.Id.Value) + "/" + Definition.DefinitionDocument.DefinitionFingerprint.Value,
            producer: "cohesive.relations/rooted-keyed-read", producerVersion: "1"));
        var request = new RelationQueryCompilationRequest(document, Definition.ShapeDocuments, Definition.RelationshipCatalogDocument);
        var materialize = ObservationMaterializer.PrepareJsonValue<TResult>();
        var resultGraph = Definition.ShapeDocuments.Single(snapshot => snapshot.Graph.Id == assembly.Shape.GraphId).Graph;
        return new(request, parameter, rows =>
        {
            var result = NestedQueryResultAssembler.Assemble(assembly, rows);
            if (result.Kind == ObservationValueKind.Null) return null;
            if (!ObservationValidator.TryValidateAgainstShape(result, resultGraph.GetShape(assembly.Shape.ShapeId), out _, resultGraph))
                throw new InvalidOperationException("Nested result violates the relation's declared output shape.");
            return materialize(result);
        });
    }
}
