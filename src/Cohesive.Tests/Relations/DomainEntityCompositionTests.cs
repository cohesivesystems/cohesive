using System.Text.Json.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Relations;

public sealed class DomainEntityCompositionTests
{
    public sealed record Parent([property: JsonPropertyName("id")] string Id);
    public sealed record Child([property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("parentId")] string ParentId);
    public sealed record Row { public string ParentId { get; init; } = ""; public string? ChildId { get; init; } }

    [Fact]
    public void Separate_relationship_is_captured_when_traversed_and_reuses_entity_graphs()
    {
        var domain = new DomainModelBuilder();
        var parents = domain.Entity<Parent>("parent");
        var children = domain.Entity<Child>("child");
        var relationship = children.References(child => child.ParentId, parents);
        var snapshot = domain.Build();
        Assert.Same(parents.Definition, snapshot.Entities[0]);
        Assert.Throws<ArgumentException>(() => domain.Entity<Parent>("parent"));
        Assert.Throws<ArgumentException>(() => children.References(child => child.ParentId.ToUpper(), parents));
        var author = RelationQuery.Expression();
        var parentShape = parents.QueryShape(author);
        children.QueryShape(author);
        Assert.Equal(parents.Definition.StateShape.QualifiedId, parentShape.Id);
        var source = author.Source(parentShape);
        var related = author.TraverseInverse(source.Node, source.Binding, relationship);
        var rows = author.Project(related.Node,
            (Parent parent, Child child) => new Row { ParentId = parent.Id, ChildId = child.Id },
            source.Binding, related.Binding);
        var query = author.BuildQuery(new("test/domain-join"), new("DomainJoin"), author.Rows(rows));
        Assert.Same(relationship.Definition, Assert.Single(author.RelationshipCatalog.Relationships));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument()));
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Diagnostics));
        Assert.Equal("parentId", relationship.Definition.SourceReference.ToString());
    }
}
