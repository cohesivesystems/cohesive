using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Semantics;

/// <summary>Inspection identities and bounded projections of the original core shape model.</summary>
public static class ShapeWorkspaceProjection
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();
    /// <summary>Returns a collision-safe qualified shape identity.</summary>
    /// <param name="shape">Original qualified identity.</param><returns>Workspace identity.</returns>
    public static string ShapeId(QualifiedShapeId shape) => "shape:" + Uri.EscapeDataString(shape.GraphId.Value) + "/" + Uri.EscapeDataString(shape.ShapeId.Value);
    /// <summary>Returns a collision-safe field identity.</summary>
    /// <param name="shape">Owning shape identity.</param><param name="field">Original field name.</param><returns>Workspace identity.</returns>
    public static string FieldId(QualifiedShapeId shape, string field) => ShapeId(shape) + "/field/" + Uri.EscapeDataString(field);
    /// <summary>Projects graph, shapes, fields and named types without introducing a second schema model.</summary>
    /// <param name="json">Original shape graph document.</param><returns>Graph view with independently addressable member views.</returns>
    /// <exception cref="JsonException">The document is absent or malformed.</exception>
    public static WorkspaceDocument Read(JsonElement json)
    {
        var document = json.Deserialize<ShapeGraphDocument>(Options) ?? throw new JsonException("Missing shape graph.");
        var validation = ShapeGraphDocumentSemanticValidator.Validate(document);
        var graph = document.Graph;
        var count = graph.Shapes.Sum(s => 1L + s.Fields.Length)
            + graph.NamedTypes.Sum(t => t is TypeDefinition.Structural structure ? 1L + structure.Fields.Length : 1L);
        if (count >= SemanticWorkspace.MaximumSubjects) throw new JsonException("Shape graph exceeds the workspace subject limit.");
        var children = ImmutableArray.CreateBuilder<WorkspaceDocument>((int)count);
        foreach (var shape in graph.Shapes)
        {
            var identity = graph.Qualify(shape.Id);

            foreach (var field in shape.Fields)
                children.Add(View(FieldId(identity, field.Name.Value), "shape-field", field, Dependencies(field.Type, graph), validation));
            children.Add(View(ShapeId(identity), "shape", shape,
                [.. shape.Fields.Select(f => Identity(FieldId(identity, f.Name.Value))),
                 .. shape.EntityType is { } entity ? new[] { Identity(EntityId(entity)) } : []], validation));
        }
        foreach (var type in graph.NamedTypes)
        {
            var id = TypeId(graph.Id, type.Id);
            if (type is TypeDefinition.Structural structure)
            {
                foreach (var field in structure.Fields)
                    children.Add(View(id + "/field/" + Uri.EscapeDataString(field.Name.Value), "type-field", field, Dependencies(field.Type, graph), validation));
                children.Add(View(id, "named-type", type,
                    structure.Fields.Select(f => Identity(id + "/field/" + Uri.EscapeDataString(f.Name.Value))), validation));
            }
            else children.Add(View(id, "named-type", type, [], validation));
        }
        var view = View("graph:" + Uri.EscapeDataString(graph.Id.Value), "shape-graph", graph,
            [.. graph.Shapes.Select(s => Identity(ShapeId(graph.Qualify(s.Id))))], validation);
        // Different query scopes may retain different subsets of the same graph. Members retain qualified identities;
        // the enclosing graph snapshot is content-addressed, never silently merged into a new semantic authority.
        return view with
        {
            Reference = view.Reference with { DefinitionId = new(view.Reference.DefinitionId.Value + "@" + view.Reference.Fingerprint!.Value) },
            Json = json.Clone(), Members = children.MoveToImmutable(),
            Source = document.Metadata.SourceUri is { Length: > 0 } source ? new(source) : null
        };
    }
    /// <summary>Creates an inspection reference where the owning declaration provides identity only.</summary>
    /// <param name="id">Workspace identity.</param><returns>Reference with no invented revision or fingerprint.</returns>
    public static WorkspaceDocumentReference Identity(string id) => new(new(id), null, null);

    /// <summary>Returns the identity of an exported owning entity.</summary><param name="entity">Canonical entity name.</param><returns>Workspace identity.</returns>
    public static string EntityId(EntityTypeName entity) => "entity:" + Uri.EscapeDataString(entity.Value);
    static string TypeId(GraphId graph, TypeId type) => "type:" + Uri.EscapeDataString(graph.Value) + "/" + Uri.EscapeDataString(type.Value);
    static IEnumerable<WorkspaceDocumentReference> Dependencies(TypeRef type, ShapeGraph graph)
    {
        switch (type)
        {
            case EntityReferenceTypeRef entity: yield return Identity(EntityId(entity.Entity)); break;
            case NamedTypeRef named: yield return Identity(TypeId(graph.Id, named.TypeId)); break;
            case ArrayTypeRef array:
                foreach (var item in Dependencies(array.ElementType, graph)) yield return item;
                break;
            case ObjectTypeRef obj:
                foreach (var field in obj.Fields)
                    foreach (var item in Dependencies(field.Type, graph)) yield return item;
                break;
        }
    }
    static WorkspaceDocument View<T>(string id, string kind, T value, IEnumerable<WorkspaceDocumentReference> dependencies, DocumentValidationResult validation) where T : class
        => new(new(new(id), null, new("sha256", "cohesive-inspection/" + kind + "/v1",
            Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(value, Options)))), kind, id,
            JsonSerializer.SerializeToElement(value, Options), null, null, validation, true, [.. dependencies], false);
}
